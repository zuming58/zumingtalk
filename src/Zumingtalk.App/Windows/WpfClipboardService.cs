using System.Windows;
using Zumingtalk.Domain.Services;

namespace Zumingtalk.App.Windows;

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        Clipboard.SetText(text);
    }
}
