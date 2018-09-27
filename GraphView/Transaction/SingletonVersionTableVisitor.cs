﻿namespace GraphView.Transaction
{
    using System.Threading;
    using System;
    using NonBlocking;
    using System.Collections.Generic;

    public interface Copyable
    {
        Copyable Copy();

        /// <summary>
        /// Copy another `Copyable` to `this`
        /// </summary>
        /// <returns>
        /// returns `true` if copy successfully
        /// `false` if `that` is not the same type as `this`
        /// </returns>
        bool CopyFrom(Copyable that);
    }

    class CachableObjectPool
    {
        private List<Copyable> objectPool = new List<Copyable>();

        public void Cache(Copyable obj)
        {
            objectPool.Add(obj);
        }
        public Copyable GetCopy(Copyable obj)
        {
            for (int i = objectPool.Count - 1; i >= 0; --i)
            {
                Copyable o = objectPool[i];
                if (o.CopyFrom(obj))
                {
                    objectPool.Remove(o);
                    return o;
                }
            }
            return obj.Copy();
        }
        public object TryGetCopy(object obj)
        {
            Copyable copyable = obj as Copyable;
            if (copyable == null) return obj;
            return this.GetCopy(copyable);
        }
        public bool TryCache(object obj)
        {
            Copyable copyable = obj as Copyable;
            if (copyable == null)
            {
                return false;
            }
            this.Cache(copyable);
            return true;
        }
    }

    internal class SingletonVersionTableVisitor : VersionTableVisitor
    {
        private readonly ConcurrentDictionary<object, ConcurrentDictionary<long, VersionEntry>> dict;
        public readonly CachableObjectPool recordPool = new CachableObjectPool();

        public SingletonVersionTableVisitor(ConcurrentDictionary<object, ConcurrentDictionary<long, VersionEntry>> dict)
        {
            this.dict = dict;
        }

        static void ResetTailEntry(VersionEntry tailEntry)
        {
            tailEntry.BeginTimestamp = VersionEntry.DEFAULT_BEGIN_TIMESTAMP;
            tailEntry.EndTimestamp = VersionEntry.DEFAULT_END_TIMESTAMP;
        }

        internal override void Visit(DeleteVersionRequest req)
        {
            ConcurrentDictionary<long, VersionEntry> versionList = req.RemoteVerList as
                ConcurrentDictionary<long, VersionEntry>;
            // Only get the version list location when version list is null
            if (versionList == null)
            {
                if (!this.dict.TryGetValue(req.RecordKey, out versionList))
                {
                    req.Result = true;
                    req.Finished = true;
                    return;
                }
            }

            VersionEntry tailEntry = null;
            versionList.TryGetValue(SingletonDictionaryVersionTable.TAIL_KEY, out tailEntry);

            // As only if a tx uploads a version entry, it will change the value tailEntry
            // Here the dirty version entry hasn't been deleted, so there will no other txs update the tailEntry
            // We don't need to take Interlocked to update its value

            // when the deleted entry is the only one in version list
            if (tailEntry.BeginTimestamp == tailEntry.EndTimestamp)
            {
                ResetTailEntry(tailEntry);
            }
            else
            {
                --tailEntry.BeginTimestamp;
            }

            VersionEntry versionEntry = null;
            long headKey = tailEntry.EndTimestamp;
            if (headKey > 0)
            {
                versionList.TryAdd(headKey - 1, versionEntry);
                tailEntry.EndTimestamp = headKey - 1;
            }

            if (versionList.TryRemove(req.VersionKey, out versionEntry))
            {
                this.recordPool.TryCache(versionEntry.Record);
                req.Result = true;
            }
            else
            {
                req.Result = false;
            }

            req.Finished = true;
        }

        internal override void Visit(InitiGetVersionListRequest req)
        {
            ConcurrentDictionary<long, VersionEntry> versionList = null;
            if (!this.dict.TryGetValue(req.RecordKey, out versionList))
            {
                ConcurrentDictionary<long, VersionEntry> newVersionList = new ConcurrentDictionary<long, VersionEntry>(32);
                // Adds a special entry whose key is -1 when the list is initialized.
                // The entry uses beginTimestamp as a pointer pointing to the newest verion in the list.
                VersionEntry entry = VersionEntry.InitEmptyVersionEntry(req.RecordKey);
                ResetTailEntry(entry);
                newVersionList.TryAdd(SingletonDictionaryVersionTable.TAIL_KEY, entry);

                if (this.dict.TryAdd(req.RecordKey, newVersionList))
                {
                    // The version list is newly created by this tx. 
                    // No meaningful versions exist, except for the artificial entry as a tail pointer. 
                    req.RemoteVerList = newVersionList;
                    req.Result = true;
                    req.Finished = true;
                    return;
                }
            }
            req.RemoteVerList = null;
            req.Result = false;
            req.Finished = true;
        }

        internal override void Visit(ReplaceVersionRequest req)
        {
            VersionEntry entry = req.RemoteVerEntry;
            if (entry == null)
            {
                ConcurrentDictionary<long, VersionEntry> versionList = null;
                if (!this.dict.TryGetValue(req.RecordKey, out versionList))
                {
                    throw new TransactionException("The specified record does not exist.");
                }

                if (!versionList.TryGetValue(req.VersionKey, out entry))
                {
                    throw new TransactionException("The specified version does not exist.");
                }
            }

            if (entry.TxId == req.SenderId && entry.EndTimestamp == req.ExpectedEndTs)
            {
                entry.Latch();
                entry.BeginTimestamp = req.BeginTs;
                entry.EndTimestamp = req.EndTs;
                entry.TxId = req.TxId;
                VersionEntry.CopyValue(entry, req.LocalVerEntry);
                entry.Unlatch();
            }

            req.Result = req.LocalVerEntry;
            req.Finished = true;
        }

        internal override void Visit(UploadVersionRequest req)
        {
            ConcurrentDictionary<long, VersionEntry> versionList = req.RemoteVerList as
                ConcurrentDictionary<long, VersionEntry>;
            if (versionList == null)
            {
                if (!this.dict.TryGetValue(req.RecordKey, out versionList))
                {
                    throw new TransactionException("The specified record does not exist.");
                }
            }

            if (versionList.TryAdd(req.VersionKey, req.VersionEntry))
            {
                // The new version has been inserted successfully. Re-directs the tail pointer to the new version.  
                VersionEntry tailEntry = null;
                versionList.TryGetValue(SingletonDictionaryVersionTable.TAIL_KEY, out tailEntry);

                lock (tailEntry) {

                long headKey = tailEntry.EndTimestamp;
                long tailKey = tailEntry.BeginTimestamp;
                // Here we use Interlocked to atomically update the tail entry, instead of ConcurrentDict.TryUpdate().
                // This is because once created, the whole tail entry always stays and is never replaced.
                // All concurrent tx's only access the tail pointer, i.e., the beginTimestamp field.  
                tailEntry.BeginTimestamp = req.VersionKey;

                // EndTimestamp (headKey) being never set means we just insert
                // the first valid version. So the headKey should be redirected.
                if (tailEntry.EndTimestamp == VersionEntry.DEFAULT_END_TIMESTAMP)
                {
                    tailEntry.EndTimestamp = tailEntry.BeginTimestamp;
                }

                VersionEntry oldVerEntry = null;
                if (versionList.Count > VersionTable.VERSION_CAPACITY)
                {
                    tailEntry.EndTimestamp = headKey + 1;
                    versionList.TryRemove(headKey, out oldVerEntry);
                    if (oldVerEntry != null)
                    {
                        this.recordPool.TryCache(oldVerEntry.Record);
                    }
                }

                req.RemoteVerEntry = oldVerEntry == null ? new VersionEntry() : oldVerEntry;
                }
                req.Result = true;
                req.Finished = true;
                return;
            }
            else
            {
                // The same version key has been added before or by a concurrent tx. 
                // The new version cannot be inserted.
                req.RemoteVerEntry = req.VersionEntry;
                req.Result = false;
                req.Finished = true;
            }
        }

        internal override void Visit(UpdateVersionMaxCommitTsRequest req)
        {
            VersionEntry verEntry = req.RemoteVerEntry;
            if (verEntry == null)
            {
                ConcurrentDictionary<long, VersionEntry> versionList = null;
                if (!this.dict.TryGetValue(req.RecordKey, out versionList))
                {
                    throw new TransactionException("The specified record does not exist.");
                }

                if (!versionList.TryGetValue(req.VersionKey, out verEntry))
                {
                    throw new TransactionException("The specified version does not exist.");
                }
            }

            verEntry.Latch();
            verEntry.MaxCommitTs = Math.Max(req.MaxCommitTs, verEntry.MaxCommitTs);
            VersionEntry.CopyValue(verEntry, req.LocalVerEntry);
            verEntry.Unlatch();

            req.Result = req.LocalVerEntry;
            req.Finished = true;
        }

        internal override void Visit(GetVersionListRequest req)
        {
            ConcurrentDictionary<long, VersionEntry> versionList = null;
            if (!this.dict.TryGetValue(req.RecordKey, out versionList))
            {
                req.RemoteVerList = null;
                req.Result = 0;
                req.Finished = true;
                return;
            }
            int entryCount = 0;

            // The value at -1 in the version list is a special entry, 
            // whose beginTimestamp points to the newest version. 
            VersionEntry tailEntry = null;
            versionList.TryGetValue(SingletonDictionaryVersionTable.TAIL_KEY, out tailEntry);
            lock (tailEntry)
            {
            long lastVersionKey = Interlocked.Read(ref tailEntry.BeginTimestamp);

            TxList<VersionEntry> localList = req.LocalContainer;
            TxList<VersionEntry> remoteList = req.RemoteContainer;
            // Only returns top 2 newest versions. This is enough for serializability. 
            // For other isolation levels, more versions may need to be returned.
            // When old versions may be truncated, it is desirable to maintain a head pointer as well,
            // so as to increase the lower bound of version keys and reduce the number of iterations. 
            while (lastVersionKey >= 0 && entryCount < 2)
            {
                VersionEntry verEntry = null;
                if (versionList.TryGetValue(lastVersionKey, out verEntry))
                {
                    verEntry.Latch();
                    VersionEntry.CopyValue(verEntry, localList[entryCount]);
                    verEntry.Unlatch();

                    // Here only add a reference to the list, no need to take the latch
                    remoteList.Add(verEntry);
                    entryCount++;

                    if (verEntry.TxId == VersionEntry.EMPTY_TXID)
                    {
                        break;
                    }
                }

                lastVersionKey--;
            }
            }

            req.RemoteVerList = versionList;
            req.Result = entryCount;
            req.Finished = true;
        }

        internal override void Visit(ReadVersionRequest req)
        {
            VersionEntry versionEntry = req.RemoteVerEntry;
            if (versionEntry == null)
            {
                ConcurrentDictionary<long, VersionEntry> versionList = null;
                if (!this.dict.TryGetValue(req.RecordKey, out versionList))
                {
                    req.Result = null;
                    req.Finished = true;
                    return;
                }

                if (!versionList.TryGetValue(req.VersionKey, out versionEntry))
                {
                    req.Result = null;
                    req.Finished = true;
                    return;
                }
            }

            versionEntry.Latch();
            VersionEntry.CopyValue(versionEntry, req.LocalVerEntry);
            versionEntry.Unlatch();

            req.Result = req.LocalVerEntry;
            req.Finished = true;
        }
    }
}
