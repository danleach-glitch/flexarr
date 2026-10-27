using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Templates;

namespace Flexarr.Api.Logging;

public static class LogFormats
{
    /// <summary>
    /// One JSON object per line, in the shape of Serilog's compact format: @t time, @m message (text as written, not
    /// quoted), @l level (left out for Information), @x exception, then the properties.
    /// </summary>
    public static ITextFormatter Json() => new RedactingFormatter(
        new ExpressionTemplate("{ {@t, @m, @l: if @l = 'Information' then undefined() else @l, @x, ..@p} }\n"));
}

/// <summary>Formats with <paramref name="inner"/>, then removes credentials from the whole line (exceptions included).</summary>
public sealed class RedactingFormatter(ITextFormatter inner) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var buffer = new StringWriter();
        inner.Format(logEvent, buffer);
        output.Write(Redactor.Redact(buffer.ToString()));
    }
}

/// <summary>Tags each entry with its <see cref="LogAreas">area</see>, which the Logs page filters by.</summary>
public sealed class AreaEnricher : ILogEventEnricher
{
    public const string Property = "Area";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var source = logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string s } ? s : null;
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(Property, LogAreas.Of(source)));
    }
}
