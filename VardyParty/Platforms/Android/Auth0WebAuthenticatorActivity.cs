using Android.App;
using Android.Content;
using Android.Content.PM;

namespace VardyParty;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = CallbackScheme)]
public class Auth0WebAuthenticatorActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
    private const string CallbackScheme = "vardyparty";
}
