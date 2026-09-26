using System.IO.Abstractions;
using System.Text;

namespace ScreenVault.Core.Infrastructure;

public sealed class AtomicFile
{
    private readonly IFileSystem _fileSystem;

    public AtomicFile(IFileSystem? fileSystem = null)
    {
        _fileSystem = fileSystem ?? new FileSystem();
    }

    public void WriteAllText(string path, string content, string? backupPath = null, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var directory = _fileSystem.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !_fileSystem.Directory.Exists(directory))
        {
            _fileSystem.Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            using (var stream = _fileSystem.FileStream.New(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (_fileSystem.File.Exists(path))
            {
                try
                {
                    if (!string.IsNullOrEmpty(backupPath))
                    {
                        var backupDir = _fileSystem.Path.GetDirectoryName(backupPath);
                        if (!string.IsNullOrEmpty(backupDir) && !_fileSystem.Directory.Exists(backupDir))
                        {
                            _fileSystem.Directory.CreateDirectory(backupDir);
                        }
                        _fileSystem.File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        _fileSystem.File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
                    }
                }
                catch (IOException)
                {
                    if (!string.IsNullOrEmpty(backupPath))
                    {
                        try { _fileSystem.File.Copy(path, backupPath, overwrite: true); } catch { /* Ignore */ }
                    }
                    _fileSystem.File.Copy(tempPath, path, overwrite: true);
                }
            }
            else
            {
                _fileSystem.File.Move(tempPath, path);
            }
        }
        finally
        {
            if (_fileSystem.File.Exists(tempPath))
            {
                try
                {
                    _fileSystem.File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }

    public string ReadAllText(string path, string? backupPath = null, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        if (_fileSystem.File.Exists(path))
        {
            return _fileSystem.File.ReadAllText(path, encoding);
        }

        if (!string.IsNullOrEmpty(backupPath) && _fileSystem.File.Exists(backupPath))
        {
            return _fileSystem.File.ReadAllText(backupPath, encoding);
        }

        throw new FileNotFoundException($"Neither primary file '{path}' nor backup file '{backupPath}' could be found.");
    }
}
