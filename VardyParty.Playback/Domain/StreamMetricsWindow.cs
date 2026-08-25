namespace VardyParty.Services
{
    public class StreamMetricsWindow
    {
        public int BufferingEvents { get; private set; }
        public List<int> BitrateReadings { get; } = new();
        public List<DateTime> ErrorTimes { get; } = new();
        public DateTime WindowStart { get; private set; } = DateTime.UtcNow;

        public void AddBufferingEvent() => BufferingEvents++;

        public void AddBitrate(int bitrate) => BitrateReadings.Add(bitrate);

        public void AddError() => ErrorTimes.Add(DateTime.UtcNow);

        public void ResetIfExpired(int windowSeconds = 60)
        {
            if ((DateTime.UtcNow - WindowStart).TotalSeconds > windowSeconds)
            {
                BufferingEvents = 0;
                BitrateReadings.Clear();
                WindowStart = DateTime.UtcNow;
            }

            ErrorTimes.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 300);
        }

        public bool IsHealthDeclined()
        {
            ResetIfExpired();

            if (BufferingEvents >= 4) return true;

            if (BitrateReadings.Count >= 3)
            {
                var lastThree = BitrateReadings.TakeLast(3).ToList();
                var avgBitrate = lastThree.Average();

                if (avgBitrate < 300) return true;

                if (lastThree.All(b => b < 500) && BitrateReadings.Count >= 10) return true;
            }

            if (ErrorTimes.Count >= 3) return true;

            return false;
        }
    }
}
