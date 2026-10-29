using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DirectoryAccess.Tests;

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, Dictionary<string, object?> Properties, Exception? Exception);
    public List<Entry> Entries { get; } = [];
    public string Dump => JsonSerializer.Serialize(Entries);
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        NullLogger<T>.Instance.BeginScope(state);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(p => p.Key, p => p.Value) : [];
        Entries.Add(new Entry(logLevel, formatter(state, exception), properties, exception));
    }
}
