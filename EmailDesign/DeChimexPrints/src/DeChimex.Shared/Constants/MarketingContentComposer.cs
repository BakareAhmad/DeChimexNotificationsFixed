using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeChimex.Shared.Constants;

/// <summary>One user-editable block. Only known fields and types are rendered.</summary>
public sealed class MarketingContentBlock
{
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string? Url { get; set; }
    public string? Alt { get; set; }
}

public sealed class MarketingContentDocument
{
    public List<MarketingContentBlock> Blocks { get; set; } = new();
}

public sealed record MarketingContentBuildResult(bool Success, string Html, string PlainText, string? Error);

/// <summary>
/// Compiles the campaign builder's small block vocabulary to email-safe markup.
/// User values are encoded and URLs are restricted to HTTPS. No HTML, CSS, or
/// arbitrary attributes from the browser are ever copied into the email.
/// </summary>
public static partial class MarketingContentComposer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 8
    };

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PersonalizationTokens();

    public static MarketingContentBuildResult Build(string? contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson) || contentJson.Length > 20_000)
            return Invalid("Add at least one content block before saving.");

        MarketingContentDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<MarketingContentDocument>(contentJson, JsonOptions);
        }
        catch (JsonException)
        {
            return Invalid("The campaign blocks could not be read. Reopen the editor and try again.");
        }

        if (document?.Blocks is null || document.Blocks.Count is < 1 or > 20)
            return Invalid("A campaign needs between 1 and 20 content blocks.");

        var html = new StringBuilder();
        var plain = new StringBuilder();
        foreach (var block in document.Blocks)
        {
            if (block is null || string.IsNullOrWhiteSpace(block.Type))
                return Invalid("One of the content blocks is missing its type.");

            switch (block.Type.Trim().ToLowerInvariant())
            {
                case "heading":
                {
                    if (!TryText(block.Text, 120, required: true, out var text, out var error)) return Invalid(error!);
                    if (!HasSupportedTokens(text, out error)) return Invalid(error!);
                    html.Append("<h2 style='margin:0 0 12px;font-size:22px;line-height:1.25;font-weight:800;color:#17161C;letter-spacing:-.02em'>")
                        .Append(EncodeLines(text)).Append("</h2>");
                    plain.AppendLine(text).AppendLine();
                    break;
                }
                case "paragraph":
                {
                    if (!TryText(block.Text, 1600, required: true, out var text, out var error)) return Invalid(error!);
                    if (!HasSupportedTokens(text, out error)) return Invalid(error!);
                    html.Append("<p style='margin:0 0 16px;font-size:15px;line-height:1.65;color:#475569'>")
                        .Append(EncodeLines(text)).Append("</p>");
                    plain.AppendLine(text).AppendLine();
                    break;
                }
                case "image":
                {
                    if (!TryUrl(block.Url, out var url)) return Invalid("Images need a secure HTTPS address.");
                    if (!TryText(block.Alt, 160, required: true, out var alt, out var error)) return Invalid("Add a short description for the image.");
                    if (!HasSupportedTokens(alt, out error)) return Invalid(error!);
                    html.Append("<p style='margin:0 0 18px;text-align:center'><img src='")
                        .Append(WebUtility.HtmlEncode(url)).Append("' alt='")
                        .Append(WebUtility.HtmlEncode(alt)).Append("' width='552' style='display:block;width:100%;max-width:552px;height:auto;border:0;margin:0 auto;border-radius:12px'></p>");
                    plain.Append("[Image: ").Append(alt).AppendLine("]").AppendLine();
                    break;
                }
                case "button":
                {
                    if (!TryText(block.Text, 60, required: true, out var text, out var error)) return Invalid("Add a label to the button.");
                    if (!HasSupportedTokens(text, out error)) return Invalid(error!);
                    if (!TryUrl(block.Url, out var url)) return Invalid("Buttons need a secure HTTPS link.");
                    html.Append("<table role='presentation' cellpadding='0' cellspacing='0' border='0' style='margin:6px 0 18px'><tr><td align='center' bgcolor='#4338CA' style='border-radius:12px;background:#4338CA'>")
                        .Append("<a href='").Append(WebUtility.HtmlEncode(url))
                        .Append("' style='display:inline-block;padding:13px 24px;font-size:15px;font-weight:700;line-height:1.2;color:#FFFFFF;text-decoration:none;border-radius:12px;background:#4338CA'>")
                        .Append(EncodeLines(text)).Append("</a></td></tr></table>");
                    plain.Append(text).Append(" ").AppendLine(url).AppendLine();
                    break;
                }
                case "divider":
                    html.Append("<hr style='border:0;border-top:1px solid #E7E4DC;margin:8px 0 18px'>");
                    plain.AppendLine();
                    break;
                default:
                    return Invalid("One of the content blocks uses an unsupported type. Remove it and add a supported block.");
            }
        }

        var safeHtml = MarketingHtmlSanitizer.Sanitize(html.ToString());
        if (string.IsNullOrWhiteSpace(safeHtml)) return Invalid("Add at least one block with readable content.");
        return new MarketingContentBuildResult(true, safeHtml, plain.ToString().Trim(), null);
    }

    private static MarketingContentBuildResult Invalid(string message) => new(false, string.Empty, string.Empty, message);

    private static bool TryText(string? value, int maxLength, bool required, out string text, out string? error)
    {
        text = value?.Trim() ?? string.Empty;
        error = null;
        if (required && text.Length == 0)
        {
            error = "Fill in the text for each heading, paragraph, image description, and button.";
            return false;
        }
        if (text.Length > maxLength)
        {
            error = $"Keep each block to {maxLength} characters or fewer.";
            return false;
        }
        return true;
    }

    private static bool TryUrl(string? value, out string url)
    {
        url = value?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        url = uri.AbsoluteUri;
        return true;
    }

    private static bool HasSupportedTokens(string value, out string? error)
    {
        error = null;
        foreach (Match match in PersonalizationTokens().Matches(value))
        {
            if (!string.Equals(match.Groups[1].Value.Trim(), "FirstName", StringComparison.Ordinal))
            {
                error = "Use only the First name personalization token in campaign text.";
                return false;
            }
        }
        return true;
    }

    private static string EncodeLines(string value)
        => WebUtility.HtmlEncode(value).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal).Replace("\r", "<br>", StringComparison.Ordinal);
}
