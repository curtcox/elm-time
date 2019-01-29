﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Kalmit.ProcessStore;
using Newtonsoft.Json;

namespace Kalmit.PersistentProcess
{
    public interface IPersistentProcess
    {
        (IReadOnlyList<string> responses, (byte[] serializedCompositionRecord, byte[] serializedCompositionRecordHash))
            ProcessEvents(IReadOnlyList<string> serializedEvents);

        (byte[] serializedCompositionRecord, byte[] serializedCompositionRecordHash) SetState(string state);

        ReductionRecord ReductionRecordForCurrentState();
    }

    //  A provisional special case for a process from an elm app. Migrations give an example of why the elm code should be modeled on the history as well.
    public class PersistentProcessWithHistoryOnFileFromElm019Code : IPersistentProcess, IDisposable
    {
        byte[] lastStateHash;

        IDisposableProcessWithCustomSerialization process;

        public PersistentProcessWithHistoryOnFileFromElm019Code(
            IProcessStoreReader storeReader,
            byte[] elmAppFile)
        {
            var elmApp =
                ElmAppWithEntryConfig.FromFiles(ZipArchive.EntriesFromZipArchive(elmAppFile).ToList());

            process =
                ProcessFromElm019Code.WithCustomSerialization(
                elmApp.ElmAppFiles,
                elmApp.EntryConfig.Value.WithCustomSerialization.Value);

            var emptyInitHash = CompositionRecord.HashFromSerialRepresentation(new byte[0]);

            string dictKeyForHash(byte[] hash) => Convert.ToBase64String(hash);

            var compositionRecords = new Dictionary<string, (byte[] compositionRecordHash, CompositionRecord compositionRecord)>();

            var compositionChain = new Stack<(byte[] hash, CompositionRecord composition)>();

            foreach (var serializedCompositionRecord in storeReader.EnumerateSerializedCompositionsRecordsReverse())
            {
                {
                    var compositionRecord = JsonConvert.DeserializeObject<CompositionRecord>(
                        System.Text.Encoding.UTF8.GetString(serializedCompositionRecord));

                    var compositionRecordHash = CompositionRecord.HashFromSerialRepresentation(serializedCompositionRecord);

                    var compositionChainElement = (compositionRecordHash, compositionRecord);

                    if (!compositionChain.Any())
                        compositionChain.Push(compositionChainElement);
                    else
                        compositionRecords[dictKeyForHash(compositionRecordHash)] = compositionChainElement;
                }

                while (true)
                {
                    var (compositionRecordHash, compositionRecord) = compositionChain.Peek();

                    var reduction = storeReader.GetReduction(compositionRecordHash);

                    if (reduction != null || emptyInitHash.SequenceEqual(compositionRecord.ParentHash))
                    {
                        if (reduction != null)
                        {
                            compositionChain.Pop();
                            process.SetSerializedState(reduction.ReducedValue);
                            lastStateHash = reduction.ReducedCompositionHash;
                        }

                        foreach (var followingComposition in compositionChain)
                        {
                            if (followingComposition.composition.SetState != null)
                                process.SetSerializedState(followingComposition.composition.SetState);

                            foreach (var appendedEvent in followingComposition.composition.AppendedEvents.EmptyIfNull())
                                process.ProcessEvent(appendedEvent);

                            lastStateHash = followingComposition.hash;
                        }

                        return;
                    }

                    var parentKey = dictKeyForHash(compositionRecord.ParentHash);

                    if (!compositionRecords.TryGetValue(parentKey, out var compositionChainElementFromPool))
                        break;

                    compositionChain.Push(compositionChainElementFromPool);
                    compositionRecords.Remove(parentKey);
                }
            }

            if (compositionChain.Any())
                throw new NotImplementedException(
                    "I did not find a reduction for any composition on the chain to the last composition (" +
                    JsonConvert.SerializeObject(compositionChain.Last().hash) +
                    ").");

            lastStateHash = emptyInitHash;
        }

        static string Serialize(CompositionRecord composition) =>
            JsonConvert.SerializeObject(composition);

        public (IReadOnlyList<string> responses, (byte[] serializedCompositionRecord, byte[] serializedCompositionRecordHash))
            ProcessEvents(IReadOnlyList<string> serializedEvents)
        {
            lock (process)
            {
                var responses =
                    serializedEvents.Select(serializedEvent => process.ProcessEvent(serializedEvent))
                    .ToList();

                var compositionRecord = new CompositionRecord
                {
                    ParentHash = lastStateHash,
                    AppendedEvents = serializedEvents,
                };

                var serializedCompositionRecord =
                    Encoding.UTF8.GetBytes(Serialize(compositionRecord));

                var compositionHash = CompositionRecord.HashFromSerialRepresentation(serializedCompositionRecord);

                lastStateHash = compositionHash;

                return (responses, (serializedCompositionRecord, compositionHash));
            }
        }

        public void Dispose() => process?.Dispose();

        public ReductionRecord ReductionRecordForCurrentState()
        {
            lock (process)
            {
                return
                    new ReductionRecord
                    {
                        ReducedCompositionHash = lastStateHash,
                        ReducedValue = process.GetSerializedState(),
                    };
            }
        }

        public (byte[] serializedCompositionRecord, byte[] serializedCompositionRecordHash) SetState(string state)
        {
            lock (process)
            {
                process.SetSerializedState(state);

                var compositionRecord = new CompositionRecord
                {
                    ParentHash = lastStateHash,
                    SetState = state,
                };

                var serializedCompositionRecord =
                    Encoding.UTF8.GetBytes(Serialize(compositionRecord));

                var compositionHash = CompositionRecord.HashFromSerialRepresentation(serializedCompositionRecord);

                lastStateHash = compositionHash;

                return (serializedCompositionRecord, compositionHash);
            }
        }
    }

    public class PersistentProcessWithControlFlowOverStoreWriter : IDisposableProcessWithCustomSerialization
    {
        IPersistentProcess process;

        IProcessStoreWriter storeWriter;

        public PersistentProcessWithControlFlowOverStoreWriter(
            IPersistentProcess process,
            IProcessStoreWriter storeWriter)
        {
            this.process = process;
            this.storeWriter = storeWriter;
        }

        public string ProcessEvent(string serializedEvent)
        {
            lock (process)
            {
                var (responses, (serializedCompositionRecord, serializedCompositionRecordHash)) =
                    process.ProcessEvents(new[] { serializedEvent });

                var response = responses.Single();

                storeWriter.AppendSerializedCompositionRecord(serializedCompositionRecord);
                storeWriter.StoreReduction(process.ReductionRecordForCurrentState());

                return response;
            }
        }

        string IProcess<string, string>.GetSerializedState() => process.ReductionRecordForCurrentState().ReducedValue;

        string IProcess<string, string>.SetSerializedState(string serializedState)
        {
            lock (process)
            {
                var (serializedCompositionRecord, serializedCompositionRecordHash) =
                    process.SetState(serializedState);

                storeWriter.AppendSerializedCompositionRecord(serializedCompositionRecord);

                return null;
            }
        }

        public void Dispose() => (process as IDisposable)?.Dispose();
    }
}