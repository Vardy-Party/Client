using System;

namespace VardyParty
{
    // Simple static holder for IServiceProvider so platform code can resolve app services.
    public static class AppServiceProvider
    {
        public static IServiceProvider? ServiceProvider { get; set; }
    }
}
