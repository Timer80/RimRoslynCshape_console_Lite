using System;
using System.Threading.Tasks;

namespace RoslynProbe.Repl
{
    public class ReplRequest
    {
        public Guid Id;
        public Func<ReplResult> Handler;
        public TaskCompletionSource<ReplResult> Tcs;
        public int TimeoutMs;
        public bool Completed;
        public bool Orphaned;
    }
}
