// The desktop sync coordinator allows one sync pass per process (by design). The server test classes drive it,
// so they run one after another instead of in parallel.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
