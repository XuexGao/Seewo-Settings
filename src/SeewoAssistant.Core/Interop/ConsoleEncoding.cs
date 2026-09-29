using System.Globalization;
using System.Text;

namespace SeewoAssistant.Core.Interop;

/// <summary>
/// The console output encoding used by tools such as <c>schtasks.exe</c> and
/// <c>sc.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// Those tools write in the console OEM code page, not UTF-8. Decoding their output as
/// UTF-8 garbles every localized status word and task name on a non-English Windows, so
/// the OEM code page is the correct choice.
/// </para>
/// <para>
/// .NET Core only ships Unicode, ASCII and Latin-1 by default. The legacy code pages
/// require <see cref="CodePagesEncodingProvider"/> to be registered, and calling
/// <see cref="Encoding.GetEncoding(int)"/> before that throws
/// <see cref="NotSupportedException"/>. That exception broke the entire startup-entry
/// scan with "No data is available for encoding 437", so the registration lives here
/// rather than being repeated at each call site.
/// </para>
/// </remarks>
internal static class ConsoleEncoding
{
    private static readonly object Gate = new();

    private static bool _registered;

    /// <summary>
    /// The OEM code page encoding, or Latin-1 when even that is unavailable.
    /// </summary>
    /// <remarks>
    /// Latin-1 is the fallback because it can decode any byte sequence without
    /// throwing. Slightly wrong characters are far better than losing the whole
    /// operation to an encoding exception.
    /// </remarks>
    internal static Encoding Oem
    {
        get
        {
            EnsureRegistered();

            try
            {
                return Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception)
            {
                return Encoding.Latin1;
            }
        }
    }

    /// <summary>Registers the legacy code-page provider exactly once.</summary>
    internal static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _registered = true;
        }
    }
}
