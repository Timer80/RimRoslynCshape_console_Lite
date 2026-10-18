using System.Collections.Generic;

namespace RoslynProbe.Repl
{
    public class ReplResult
    {
        public bool Ok;
        public object Value;
        public string Error;
        public bool TimedOut;
        /// <summary>本次提交自动补的 using 命名空间（T5，仅本次注入、绝不写回 usings 表）；无则为 null。</summary>
        public List<string> AutoUsings;

        public static ReplResult Success(object value = null)
        {
            return new ReplResult { Ok = true, Value = value };
        }

        public static ReplResult Failure(string error)
        {
            return new ReplResult { Ok = false, Error = error };
        }
    }
}
