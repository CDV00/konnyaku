using System.Collections.Frozen;
using System.Net;
using System.Text.RegularExpressions;
using Konnyaku.Notification.Application;

namespace Konnyaku.Notification.Infrastructure;

/// <summary>
/// Templates shipped as files under <c>Templates/&lt;channel&gt;/&lt;key&gt;/&lt;locale&gt;.{subject.txt,html,txt}</c>, wrapped in
/// <c>Templates/&lt;channel&gt;/_layout/&lt;locale&gt;.{html,txt}</c>. Placeholders: <c>{{name}}</c> (HTML-escaped in HTML parts),
/// sections <c>{{#name}}…{{/name}}</c> (shown when the value is not empty) and <c>{{^name}}…{{/name}}</c> (when it is).
/// </summary>
public sealed partial class EmbeddedTemplateRenderer : ITemplateRenderer
{
    private const string LayoutKey = "_layout";
    private readonly FrozenDictionary<string, string> _files;

    public EmbeddedTemplateRenderer()
    {
        var assembly = typeof(EmbeddedTemplateRenderer).Assembly;
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("templates", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            files[name.Replace('\\', '/')] = reader.ReadToEnd();
        }

        _files = files.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Files => _files.Keys;

    public RenderedContent? Render(string channel, string templateKey, IReadOnlyList<string> locales, IReadOnlyDictionary<string, string> values)
    {
        foreach (var locale in locales)
        {
            if (Find(channel, templateKey, locale, "subject.txt") is not { } subject ||
                Find(channel, templateKey, locale, "html") is not { } html ||
                Find(channel, templateKey, locale, "txt") is not { } text)
            {
                continue;
            }

            var renderedSubject = Fill(subject, values, escape: false).Trim();
            var htmlBody = Fill(html, values, escape: true);
            var textBody = Fill(text, values, escape: false);
            var layoutValues = new Dictionary<string, string>(values) { ["subject"] = renderedSubject };
            if (Find(channel, LayoutKey, locale, "html") is { } htmlLayout)
            {
                htmlBody = Fill(htmlLayout, layoutValues, escape: true).Replace("{{{content}}}", htmlBody, StringComparison.Ordinal);
            }

            if (Find(channel, LayoutKey, locale, "txt") is { } textLayout)
            {
                textBody = Fill(textLayout, layoutValues, escape: false).Replace("{{{content}}}", textBody.Trim(), StringComparison.Ordinal);
            }

            return new RenderedContent(renderedSubject, htmlBody, textBody.Trim() + "\n");
        }

        return null;
    }

    private string? Find(string channel, string key, string locale, string part) =>
        _files.GetValueOrDefault($"templates/{channel}/{key}/{locale}.{part}");

    internal static string Fill(string template, IReadOnlyDictionary<string, string> values, bool escape)
    {
        var withSections = SectionPattern().Replace(template, m =>
        {
            var present = !string.IsNullOrEmpty(values.GetValueOrDefault(m.Groups["name"].Value));
            var show = m.Groups["kind"].Value == "#" ? present : !present;
            return show ? m.Groups["body"].Value : string.Empty;
        });
        return PlaceholderPattern().Replace(withSections, m =>
        {
            var value = values.GetValueOrDefault(m.Groups["name"].Value) ?? string.Empty;
            return escape ? WebUtility.HtmlEncode(value) : value;
        });
    }

    [GeneratedRegex(@"\{\{(?<kind>[#^])(?<name>[A-Za-z0-9_]+)\}\}(?<body>.*?)\{\{/\k<name>\}\}", RegexOptions.Singleline)]
    private static partial Regex SectionPattern();

    // {{{content}}} (three braces) is left for the layout step.
    [GeneratedRegex(@"(?<!\{)\{\{(?<name>[A-Za-z0-9_]+)\}\}(?!\})")]
    private static partial Regex PlaceholderPattern();
}
