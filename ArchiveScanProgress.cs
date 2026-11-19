namespace SurfArchive;

internal sealed class ArchiveScanProgress(string phase)
{
    private readonly object sync = new();
    private int files;
    private int folders;
    private long bytes;

    public void SetPhase(string value)
    {
        lock (sync)
            phase = value;
    }

    public void Add(bool isDirectory, long size)
    {
        lock (sync)
        {
            if (isDirectory)
                folders++;
            else
            {
                files++;
                bytes += size;
            }
        }
    }

    public (string Phase, int Files, int Folders, long Bytes) Read()
    {
        lock (sync)
            return (phase, files, folders, bytes);
    }
}
