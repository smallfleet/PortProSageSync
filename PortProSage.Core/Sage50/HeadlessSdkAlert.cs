using Microsoft.Extensions.Logging;
using Simply.Domain.Utility;
using SimplySDK.Support;

namespace PortProSage.Core.Sage50;

/// <summary>
/// The default SDKAlert implementation throws an exception for every alert,
/// including ordinary Yes/No confirmations Sage 50's desktop UI would normally
/// show as a dialog (e.g. "The date for this transaction precedes the session
/// date... Are you sure you want to continue?", thrown as
/// SimplySDK.AlertNotImplementedException - confirmed live 2026-08-04 while
/// posting historical invoices, exactly the kind of prior-period date this
/// service is expected to post for a backfill).
///
/// SDKInstanceManager.SetAlertImplementation exists precisely so a headless/
/// automated caller can supply its own answer instead of a human clicking a
/// button - register an instance of this class once at startup. Only AskAlert
/// (documented as a plain Yes/No confirmation) is overridden to auto-answer
/// YES; every other alert type (StopAlert, WarnAlert, etc. - genuine errors or
/// stronger confirmations) is left to the base class's default throwing
/// behavior, so this doesn't silently swallow anything more than ordinary
/// "are you sure" confirmations.
/// </summary>
internal class HeadlessSdkAlert : SDKAlert
{
    private readonly ILogger _logger;

    public HeadlessSdkAlert(ILogger logger)
    {
        _logger = logger;
    }

    public override AlertResult AskAlert(SimplyMessage message)
    {
        _logger.LogWarning("Sage 50 SDK confirmation auto-answered YES (headless mode): {Message}", message?.Message);
        return AlertResult.YES;
    }

    public override AlertResult AskAlert(SimplyMessage message, IntPtr owner)
    {
        _logger.LogWarning("Sage 50 SDK confirmation auto-answered YES (headless mode): {Message}", message?.Message);
        return AlertResult.YES;
    }
}
