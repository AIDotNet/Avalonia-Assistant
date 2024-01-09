using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins
{
    public class WindowsPlugin
    {
        private readonly Kernel _kernel;
        public WindowsPlugin(Kernel kernel)
        {
            _kernel = kernel;
        }
    }
}
