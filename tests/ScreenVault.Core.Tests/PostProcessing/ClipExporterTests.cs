using System.IO.Abstractions.TestingHelpers;
using ScreenVault.Core.PostProcessing;
using ScreenVault.Core.Sessions;
using Shouldly;
using Xunit;

namespace ScreenVault.Core.Tests.PostProcessing;

public sealed class ClipExporterTests
{
    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly Dictionary<string, SessionManifest> _sessions = new();

        public void AddSession(SessionManifest manifest) => _sessions[manifest.SessionId] = manifest;

        public SessionManifest? Load(string sessionId) =>
            _sessions.TryGetValue(sessionId, out var m) ? m : null;

        public void Save(SessionManifest manifest, IEnumerable<string>? mirrorDirectories = null) =>
            _sessions[manifest.SessionId] = manifest;

        public void Delete(string sessionId, IEnumerable<string>? mirrorDirectories = null) =>
            _sessions.Remove(sessionId);

        public SessionManifest? LoadFromPath(string manifestPath) => null;
        public IReadOnlyList<SessionManifest> LoadAllCanonical() => _sessions.Values.ToList();
        public string GetCanonicalPath(string sessionId) => $@"C:\ScreenVault\sessions\{sessionId}.json";
    }

    [Fact]
    public async Task ExportClipAsync_SessionNotFound_ReturnsError()
    {
        var store = new FakeSessionStore();
        var mockFs = new MockFileSystem();
        var exporter = new ClipExporter(store, mockFs, "ffmpeg.exe");

        var options = new ClipExportOptions(
            SessionId: "nonexistent",
            StartOffsetSec: 10,
            EndOffsetSec: 20,
            DestinationPath: @"C:\clips\clip.mp4");

        var result = await exporter.ExportClipAsync(options);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    public async Task ExportClipAsync_InvalidOffsets_ReturnsError()
    {
        var store = new FakeSessionStore();
        var mockFs = new MockFileSystem();
        var exporter = new ClipExporter(store, mockFs, "ffmpeg.exe");

        var manifest = new SessionManifest
        {
            SessionId = "session_123",
            StartedAtUtc = DateTime.UtcNow
        };
        store.AddSession(manifest);

        var options = new ClipExportOptions(
            SessionId: "session_123",
            StartOffsetSec: 30,
            EndOffsetSec: 20, // End before start
            DestinationPath: @"C:\clips\clip.mp4");

        var result = await exporter.ExportClipAsync(options);

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Invalid start or end offset");
    }
}
