// The tests share one UI thread and a few program-wide objects (shortcut keys, display preferences):
// they run one after another so that one test can never see what another one has just changed.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
