using System.Security.Cryptography;
using System.Text;

namespace Pvz.Infrastructure;

public sealed class SingleInstanceLock : IDisposable
{
    private readonly Mutex mutex;
    public bool Acquired { get; }

    public SingleInstanceLock(string databasePath)
    {
        var canonical = Path.GetFullPath(databasePath).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        mutex = new Mutex(false, @"Local\PvzCourse-" + key);
        try { Acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Acquired = true; }
    }

    public void Dispose()
    {
        if (Acquired) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
