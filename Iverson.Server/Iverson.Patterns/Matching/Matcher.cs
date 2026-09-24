// Port of Trino 483 io.trino.operator.window.matcher.Matcher (Apache-2.0); see THIRD-PARTY-NOTICES.md.
using Iverson.Patterns.Compilation;

namespace Iverson.Patterns.Matching;

/// <summary>
/// Runs a compiled pattern program over the input as a set of prioritized threads (a Pike VM), returning the
/// preferred match starting at the pattern start.
/// <para>
/// Deviations from Trino: <c>advanceAndSchedule</c> uses an explicit stack instead of recursion (its push order
/// keeps Trino's thread priority order); aggregations are not carried per thread (the label evaluator and
/// <see cref="ThreadEquivalence"/> recompute them from the matched labels); every instruction processed and every
/// label evaluation is one <see cref="PatternBudget.Step"/>, and every thread allocation checks
/// <see cref="PatternBudget.MaxActiveThreads"/>; memory accounting is dropped.
/// </para>
/// </summary>
internal sealed class Matcher(Instruction[] program, ThreadEquivalence equivalence)
{
    private sealed class Runtime
    {
        public Runtime(Instruction[] program, int inputLength, bool matchingAtPartitionStart)
        {
            int initialCapacity = 2 * program.Length;
            Threads = new IntList(initialCapacity);
            _freeThreadIds = new IntStack(initialCapacity);
            Captures = new Captures(initialCapacity, MinSlotCount(program), MinLabelCount(program));
            InputLength = inputLength;
            MatchingAtPartitionStart = matchingAtPartitionStart;

            ThreadsAtInstructions = new IntMultimap(program.Length, program.Length);
            _threadsToKill = new IntList(initialCapacity);
        }

        // a helper structure for identifying equivalent threads
        // program pointer (instruction) --> list of threads that have reached this instruction
        public IntMultimap ThreadsAtInstructions { get; }

        // threads that should be killed as determined by the current iteration of the main loop
        // they are killed after the iteration so that they can be used to kill other threads while the iteration lasts
        private readonly IntList _threadsToKill;

        public IntList Threads { get; }
        private readonly IntStack _freeThreadIds;
        private int _newThreadId;
        public int InputLength { get; }
        public bool MatchingAtPartitionStart { get; }
        public Captures Captures { get; }

        /// <summary>The explicit stack of <see cref="AdvanceAndSchedule"/>; empty between calls.</summary>
        public Stack<Pending> PendingStack { get; } = new();

        public int ForkThread(int parent, PatternBudget budget)
        {
            int child = NewThread(budget);
            Captures.Copy(parent, child);
            return child;
        }

        public int NewThread(PatternBudget budget)
        {
            int threadId = _freeThreadIds.Size > 0 ? _freeThreadIds.Pop() : _newThreadId++;
            // allocated thread ids minus freed ones
            budget.CheckActiveThreads(_newThreadId - _freeThreadIds.Size);
            return threadId;
        }

        public void ScheduleKill(int threadId)
        {
            _threadsToKill.Add(threadId);
        }

        public void KillThreads()
        {
            for (int i = 0; i < _threadsToKill.Size; i++)
            {
                KillThread(_threadsToKill.Get(i));
            }

            _threadsToKill.Clear();
        }

        private void KillThread(int threadId)
        {
            _freeThreadIds.Push(threadId);
            Captures.Release(threadId);
        }

        /// <summary>Trino's <c>Program.getMinSlotCount</c>: the number of <c>SAVE</c> instructions.</summary>
        private static int MinSlotCount(Instruction[] program) =>
            program.Count(i => i.Kind is InstructionKind.ExclusionStart or InstructionKind.ExclusionEnd);

        /// <summary>Trino's <c>Program.getMinLabelCount</c>: the number of <c>MATCH_LABEL</c> instructions.</summary>
        private static int MinLabelCount(Instruction[] program) =>
            program.Count(i => i.Kind == InstructionKind.MatchLabel);
    }

    public MatchResult Run(ILabelEvaluator labelEvaluator, PatternBudget budget)
    {
        var current = new IntList(program.Length);
        var next = new IntList(program.Length);

        int inputLength = labelEvaluator.InputLength;
        bool matchingAtPartitionStart = labelEvaluator.MatchingAtPartitionStart;

        var runtime = new Runtime(program, inputLength, matchingAtPartitionStart);

        AdvanceAndSchedule(current, runtime.NewThread(budget), 0, 0, runtime, budget);

        var result = MatchResult.NoMatch;

        for (int index = 0; index < inputLength; index++)
        {
            if (current.Size == 0)
            {
                // no match found -- all threads are dead
                break;
            }

            bool matched = false;
            // For every existing thread, consume the label if possible. Otherwise, kill the thread.
            // After consuming the label, advance to the next `MATCH_LABEL`. Collect the advanced threads in `next`,
            // which will be the starting point for the next iteration.

            // clear the structure for new input index
            runtime.ThreadsAtInstructions.Clear();
            runtime.KillThreads();

            for (int i = 0; i < current.Size; i++)
            {
                int threadId = current.Get(i);
                int pointer = runtime.Threads.Get(threadId);
                var instruction = program[pointer];
                switch (instruction.Kind)
                {
                    case InstructionKind.MatchLabel:
                        int label = instruction.First;
                        // save the label before evaluating the defining condition, because evaluating assumes that the label is tentatively matched
                        // - if the condition is true, the label is already saved
                        // - if the condition is false, the thread is killed along with its captures, so the incorrectly saved label does not matter
                        runtime.Captures.SaveLabel(threadId, label);
                        budget.Step();
                        if (labelEvaluator.EvaluateLabel(runtime.Captures.GetLabels(threadId)))
                        {
                            AdvanceAndSchedule(next, threadId, pointer + 1, index + 1, runtime, budget);
                        }
                        else
                        {
                            runtime.ScheduleKill(threadId);
                        }

                        break;
                    case InstructionKind.Done:
                        matched = true;
                        result = new MatchResult(true, runtime.Captures.GetLabels(threadId), runtime.Captures.GetCaptures(threadId));
                        runtime.ScheduleKill(threadId);
                        break;
                    default:
                        throw new NotSupportedException("not yet implemented");
                }

                if (matched)
                {
                    // do not process the following threads, because they are on less preferred paths than the match found
                    for (int j = i + 1; j < current.Size; j++)
                    {
                        runtime.ScheduleKill(current.Get(j));
                    }

                    break;
                }
            }

            var temp = current;
            temp.Clear();
            current = next;
            next = temp;
        }

        // handle the case when the program still has instructions to process after consuming the whole input
        for (int i = 0; i < current.Size; i++)
        {
            int threadId = current.Get(i);
            if (program[runtime.Threads.Get(threadId)].Kind == InstructionKind.Done)
            {
                result = new MatchResult(true, runtime.Captures.GetLabels(threadId), runtime.Captures.GetCaptures(threadId));
                break;
            }
        }

        return result;
    }

    private readonly record struct Pending(int Thread, int Pointer);

    /// <summary>
    /// For a particular thread identified by <paramref name="threadId"/>, process consecutive instructions of the
    /// program, from the instruction at <paramref name="pointer"/> up to the next instruction which consumes a label
    /// or to the program end. The resulting thread state (the pointer of the first not processed instruction) is
    /// recorded in <paramref name="next"/>. There might be multiple threads recorded in <paramref name="next"/>, as a
    /// result of the instruction <c>SPLIT</c>.
    /// </summary>
    private void AdvanceAndSchedule(IntList next, int threadId, int pointer, int inputIndex, Runtime runtime, PatternBudget budget)
    {
        var stack = runtime.PendingStack;           // a Stack<Pending> owned by Runtime, empty between calls
        stack.Push(new Pending(threadId, pointer));
        while (stack.Count > 0)
        {
            var (thread, at) = stack.Pop();
            budget.Step();

            // avoid empty loops and exponential processing: kill a thread equivalent to one already here
            var threadsHere = runtime.ThreadsAtInstructions.GetArrayView(at);
            bool killed = false;
            for (int i = 0; i < threadsHere.Length; i++)
            {
                int other = threadsHere[i];
                if (equivalence.Equivalent(other, runtime.Captures.GetLabels(other), thread, runtime.Captures.GetLabels(thread), at))
                {
                    runtime.ScheduleKill(thread);
                    killed = true;
                    break;
                }
            }
            if (killed) continue;
            runtime.ThreadsAtInstructions.Add(at, thread);

            var instruction = program[at];
            switch (instruction.Kind)
            {
                case InstructionKind.MatchStart:
                    if (inputIndex == 0 && runtime.MatchingAtPartitionStart) stack.Push(new Pending(thread, at + 1));
                    else runtime.ScheduleKill(thread);
                    break;
                case InstructionKind.MatchEnd:
                    if (inputIndex == runtime.InputLength) stack.Push(new Pending(thread, at + 1));
                    else runtime.ScheduleKill(thread);
                    break;
                case InstructionKind.Jump:
                    stack.Push(new Pending(thread, instruction.First));
                    break;
                case InstructionKind.Split:
                    int forked = runtime.ForkThread(thread, budget);        // fork BEFORE the first branch, as Trino
                    stack.Push(new Pending(forked, instruction.Second));    // pushed first → processed after
                    stack.Push(new Pending(thread, instruction.First));     // the whole first branch completes first
                    break;
                case InstructionKind.ExclusionStart:
                case InstructionKind.ExclusionEnd:
                    runtime.Captures.Save(thread, inputIndex);
                    stack.Push(new Pending(thread, at + 1));
                    break;
                default: // MatchLabel or Done
                    runtime.Threads.Set(thread, at);
                    next.Add(thread);
                    break;
            }
        }
    }
}
