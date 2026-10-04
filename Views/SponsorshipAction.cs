using System;
using System.Threading;
using System.Threading.Tasks;

namespace IDVBuff.Views;

// Browser and UI callbacks are injected so offline tests never launch a URI.
internal sealed class SponsorshipAction
{
    internal const string Url = "https://afdian.com/a/xigefuli";
    private readonly Func<Uri, Task<bool>> _launch;
    private readonly Action<bool> _setEnabled;
    private readonly Func<Task> _showError;
    private int _busy;

    internal SponsorshipAction(Func<Uri, Task<bool>> launch, Action<bool> setEnabled, Func<Task> showError)
    {
        _launch = launch;
        _setEnabled = setEnabled;
        _showError = showError;
    }

    internal async Task ClickAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            _setEnabled(false);
            var opened = false;
            try { opened = await _launch(new Uri(Url)); }
            catch (Exception) { /* Shell failure leaves the settings page usable. */ }
            finally { _setEnabled(true); }
            if (!opened)
            {
                try { await _showError(); }
                catch (Exception) { /* Navigation or another dialog may prevent showing feedback. */ }
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
