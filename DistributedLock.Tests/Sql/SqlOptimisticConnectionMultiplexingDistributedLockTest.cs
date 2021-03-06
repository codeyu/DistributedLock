﻿using Medallion.Threading.Sql;
using Medallion.Threading.Sql.ConnectionMultiplexing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Medallion.Threading.Tests.Sql
{
    [TestClass]
    public class SqlOptimisticConnectionMultiplexingDistributedLockTest : DistributedLockTestBase
    {
        [TestMethod]
        public void TestCleanup()
        {
            var originalInterval = MultiplexedConnectionLockPool.CleanupIntervalSeconds;
            MultiplexedConnectionLockPool.CleanupIntervalSeconds = 1;
            try
            {
                var lock1 = this.CreateLock(nameof(TestCleanup));
                var lock2 = this.CreateLock(nameof(TestCleanup));
                var handleReference = this.TestCleanupHelper(lock1, lock2);
                
                GC.Collect();
                GC.WaitForPendingFinalizers();
                handleReference.IsAlive.ShouldEqual(false);
                Thread.Sleep(TimeSpan.FromSeconds(5));

                using (var handle = lock2.TryAcquire())
                {
                    Assert.IsNotNull(handle);
                }
            }
            finally
            {
                MultiplexedConnectionLockPool.CleanupIntervalSeconds = originalInterval;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // need to isolate for GC
        private WeakReference TestCleanupHelper(IDistributedLock lock1, IDistributedLock lock2)
        {
            var handle = lock1.Acquire();

            Assert.IsNull(lock2.TryAcquireAsync().Result);

            return new WeakReference(handle);
        }
        
        /// <summary>
        /// This method demonstrates how <see cref="SqlDistributedLockConnectionStrategy.OptimisticConnectionMultiplexing"/>
        /// can be used to hold many locks concurrently on one underlying connection.
        /// 
        /// Note: I would like this test to actually leverage multiple threads, but this runs into issues because the current
        /// implementation of optimistic multiplexing only makes one attempt to use a shared lock before opening a new connection.
        /// This runs into problems because the attempt to use a shared lock can fail if, for example, a lock is being released on
        /// that connection which means that the mutex for the connection can't be acquired without waiting. Once something like
        /// this happens, we try to open a new connection which times out due to pool size limits
        /// </summary>
        [TestMethod]
        public void TestHighConcurrencyWithSmallPool()
        {
            var connectionString = new SqlConnectionStringBuilder(SqlDistributedLockTest.ConnectionString) { MaxPoolSize = 1 }.ConnectionString;

            Func<Task> test = async () =>
            {
                var random = new Random(Guid.NewGuid().GetHashCode());

                var heldLocks = new Dictionary<string, IDisposable>();
                for (var i = 0; i < 1000; ++i)
                {
                    var lockName = $"{nameof(TestHighConcurrencyWithSmallPool)}_{random.Next(20)}";
                    IDisposable existingHandle;
                    if (heldLocks.TryGetValue(lockName, out existingHandle))
                    {
                        existingHandle.Dispose();
                        heldLocks.Remove(lockName);
                    }
                    else
                    {
                        var @lock = new SqlDistributedLock(lockName, connectionString, SqlDistributedLockConnectionStrategy.OptimisticConnectionMultiplexing);
                        var handle = await @lock.TryAcquireAsync();
                        if (handle != null) { heldLocks.Add(lockName, handle); }
                    }
                }
            };

            Task.Run(test).Wait(TimeSpan.FromSeconds(10)).ShouldEqual(true);
        }

        internal override IDistributedLock CreateLock(string name)
            => new SqlDistributedLock(name, SqlDistributedLockTest.ConnectionString, SqlDistributedLockConnectionStrategy.OptimisticConnectionMultiplexing);

        internal override string GetSafeLockName(string name) => SqlDistributedLock.GetSafeLockName(name);

        /// <summary>
        /// The default abandonment test doesn't work with multiplexing because the cleanup timer must come
        /// around. <see cref="TestCleanup"/> demonstrates this functionality instead
        /// </summary>
        internal override bool SupportsInProcessAbandonment => false;
    }
}
