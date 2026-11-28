namespace PersonalFlow.Core;

public sealed record FieldResolution<T>(T Value, bool HasConflict, T Base, T Local, T Server);

public static class FieldMerge
{
    public static FieldResolution<T> Resolve<T>(T baseline, T local, T server)
    {
        var comparer = EqualityComparer<T>.Default;
        if (comparer.Equals(local, server) || comparer.Equals(baseline, local))
            return new(server, false, baseline, local, server);
        if (comparer.Equals(baseline, server))
            return new(local, false, baseline, local, server);
        return new(server, true, baseline, local, server);
    }
}
