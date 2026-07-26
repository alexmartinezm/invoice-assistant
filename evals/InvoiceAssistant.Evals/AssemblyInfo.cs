// One host, one database, one recorder: cases must not interleave. The suite is also budgeted per
// run, and a deterministic order keeps token spend comparable between runs.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
