namespace NzbDrone.Core.Download.Clients.NzbVortex
{
    public enum NzbVortexDownloadStatus
    {
        Grabbing,
        Queued,
        Paused,
        Checking,
        Downloading,
        QuickCheck,
        Verifying,
        Repairing,
        Fetching,       // Fetching additional blocks
        Extracting,
        Moving,
        Running,        // Running PP Script
        Completed,
        Failed
    }
}
