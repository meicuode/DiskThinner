// 在测试盘上要核对可用空间的实际变化：测试之间不能同时写盘，所以不并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
