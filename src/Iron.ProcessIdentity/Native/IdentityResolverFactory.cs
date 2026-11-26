namespace Iron.ProcessIdentity;

/// <summary>Creates the native resolver where it can run (Windows). Stage 1 never calls this: mock rows use <see cref="IdentityComposer.ComposeMock"/>.</summary>
public static class IdentityResolverFactory
{
    public static ProcessIdentityResolver? TryCreateNative(ResolverOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return new ProcessIdentityResolver(new WindowsProcessQuery(), options);
    }
}
