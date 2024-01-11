using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins
{
    /// <summary>
    /// 操作系统类型
    /// </summary>
    public class OSAttribute : Attribute
    {
        public OSAttribute(OSType osType)
        {
            OsType = osType;
        }
        public OSType OsType { get; set; }
    }



    public enum OSType
    {
        All,
        Windows,
        Linux,
        OSX
    }
}
