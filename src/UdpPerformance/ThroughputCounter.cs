using System.Threading;

namespace UdpPerformance
{
    public class ThroughputCounter
    {
        private long _deltaCount;

        public void Add(long value)
        {
            Interlocked.Add(ref _deltaCount, value);
        }

        public long SampleAndReset()
        {
            return Interlocked.Exchange(ref _deltaCount, 0);
        }
    }
}
