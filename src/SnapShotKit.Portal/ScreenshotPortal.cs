using Tmds.DBus.Protocol;

namespace SnapShotKit.Portal;

/// <summary>Wraps org.freedesktop.portal.Screenshot.</summary>
public sealed class ScreenshotPortal(PortalClient portal)
{
    const string Interface = "org.freedesktop.portal.Screenshot";

    public Task<uint> GetVersionAsync() => portal.GetVersionAsync(Interface);

    /// <param name="interactive">
    /// When false the portal grabs the whole screen straight away, prompting for permission if it
    /// decides permission is needed. When true the compositor shows its own picker UI first.
    /// </param>
    public async Task<ScreenshotResult> CaptureAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        var options = new Dictionary<string, VariantValue>
        {
            ["interactive"] = VariantValue.Bool(interactive)
        };

        var response = await portal.CallRequestAsync((ref MessageWriter writer, string handleToken) =>
        {
            options["handle_token"] = VariantValue.String(handleToken);

            writer.WriteMethodCallHeader(PortalClient.PortalService, PortalClient.PortalObject, Interface, "Screenshot", "sa{sv}");
            writer.WriteString(string.Empty); // parent_window: no parent, this is a headless request
            writer.WriteDictionary(options);
        }, cancellationToken);

        if (response.Status != PortalResponseStatus.Success)
        {
            return new ScreenshotResult(response.Status, null);
        }

        if (!response.Results.TryGetValue("uri", out var uri))
        {
            throw new InvalidOperationException("Portal reported success but returned no 'uri' in the response.");
        }

        return new ScreenshotResult(response.Status, new Uri(uri.GetString()).LocalPath);
    }
}

public readonly record struct ScreenshotResult(PortalResponseStatus Status, string? Path)
{
    public bool IsSuccess => Status == PortalResponseStatus.Success && Path is not null;
}
