using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace UdpPerformance
{
    internal sealed class UdpAwaitableSocketAsyncEventArgs : SocketAsyncEventArgs, IValueTaskSource<int>
    {
        private static readonly Action<object?> _completedSentinel = new Action<object?>(state => throw new InvalidOperationException("Task misuse"));

        // This _token is a basic attempt to protect against misuse of the value tasks we return from this source.
        // Not perfect, intended to be 'best effort'.
        private short _token;
        private Action<object?>? _continuation;

        // Note the use of 'unsafeSuppressExecutionContextFlow'; this is an optimisation new to .NET5. We are not concerned with execution context preservation
        // in our example, so we can disable it for a slight perf boost.
        public UdpAwaitableSocketAsyncEventArgs()
            : base()
        {
        }

        public ValueTask<int> DoReceiveFromAsync(Socket socket)
        {
            // Call our socket method to do the receive.
            if (socket.ReceiveFromAsync(this))
            {
                // ReceiveFromAsync will return true if we are going to complete later.
                // So we return a ValueTask, passing 'this' (our IValueTaskSource) 
                // to the constructor. We will then tell the ValueTask when we are 'done'.
                return new ValueTask<int>(this, _token);
            }

            // If our socket method returns false, it means the call already complete synchronously; for example
            // if there is data in the socket buffer all ready to go, we'll usually complete this way.            
            return CompleteSynchronously();
        }

        public ValueTask<int> DoSendToAsync(Socket socket)
        {
            // Send looks very similar to send, just calling a different method on the socket.
            if (socket.SendToAsync(this))
            {
                return new ValueTask<int>(this, _token);
            }

            return CompleteSynchronously();
        }

        private ValueTask<int> CompleteSynchronously()
        {
            // Completing synchronously, so we don't need to preserve the 
            // async bits.
            Reset();

            var error = SocketError;
            if (error == SocketError.Success)
            {
                // Return a ValueTask directly, in a no-alloc operation.
                return new ValueTask<int>(BytesTransferred);
            }

            // Fail synchronously.
            return new ValueTask<int>(new SocketException((int)error).ErrorCode);
        }

        // This method is called by the base class when our async socket operation completes. 
        // The goal here is to wake up our 
        protected override void OnCompleted(SocketAsyncEventArgs e)
        {
            Action<object?>? c = _continuation;

            // This Interlocked.Exchange is intended to ensure that only one path can end up invoking the
            // continuation that completes the ValueTask. We swap for our _completedSentinel, and only proceed if
            // there was a continuation action present in that field.
            if (c != null || (c = Interlocked.CompareExchange(ref _continuation, _completedSentinel, null)) != null)
            {
                object? continuationState = UserToken;
                UserToken = null;

                // Mark us as done.
                _continuation = _completedSentinel;

                // Invoke the continuation. Because this completion is, by its nature, happening asynchronously,
                // we don't need to force an async invoke.
                InvokeContinuation(c.Invoke, continuationState, forceAsync: false);
            }
        }

        // This method is invoked if someone calls ValueTask.IsCompleted (for example) and when the operation completes, and needs to indicate the
        // state of the current operation.
        public ValueTaskSourceStatus GetStatus(short token)
        {
            if (token != _token)
            {
                ThrowMisuseException();
            }

            // If _continuation isn't _completedSentinel, we're still going.
            return !ReferenceEquals(_continuation, _completedSentinel) ? ValueTaskSourceStatus.Pending :
                    SocketError == SocketError.Success ? ValueTaskSourceStatus.Succeeded :
                    ValueTaskSourceStatus.Faulted;
        }

        // This method is only called once per ValueTask, once GetStatus returns something other than
        // ValueTaskSourceStatus.Pending.
        public int GetResult(short token)
        {
            // Detect multiple awaits on a single ValueTask.
            if (token != _token)
            {
                ThrowMisuseException();
            }

            // We're done, reset.
            Reset();

            // Now we just return the result (or throw if there was an error).
            var error = SocketError;
            if (error == SocketError.Success)
            {
                return BytesTransferred;
            }

            throw new SocketException((int)error);
        }

        // This is called when someone awaits on the ValueTask, and tells us what method to call to complete.
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            if (token != _token)
            {
                ThrowMisuseException();
            }

            UserToken = state;

            // Do the exchange so we know we're the only ones that could invoke the continuation.
            Action<object>? prevContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);

            // Check whether we've already finished.
            if (ReferenceEquals(prevContinuation, _completedSentinel))
            {
                // This means the operation has already completed; most likely because we completed before
                // we could attach the continuation.
                // Don't need to store the user token.
                UserToken = null;

                // We need to set forceAsync here and dispatch on the ThreadPool, otherwise
                // we can hit a stackoverflow!
                InvokeContinuation(continuation.Invoke, state, forceAsync: true);
            }
            else if (prevContinuation != null)
            {
                throw new InvalidOperationException("Continuation being attached more than once.");
            }
        }

        private void InvokeContinuation(WaitCallback continuation, object? state, bool forceAsync)
        {
            if (forceAsync)
            {
                // Dispatch the operation on the thread pool.
                ThreadPool.UnsafeQueueUserWorkItem(continuation, state);
            }
            else
            {
                // Just complete the continuation inline (on the IO thread that completed the socket operation).
                continuation(state);
            }
        }

        private void Reset()
        {
            // Increment our token for the next operation.
            _token++;
            _continuation = null;
        }

        private static void ThrowMisuseException()
        {
            throw new InvalidOperationException("ValueTask mis-use; multiple await?");
        }
    }
}
