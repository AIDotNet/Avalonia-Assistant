using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins.Attributes
{
    public static class OSExtensions
    {
        /// <summary>
        /// 根据SK特性注入插件，根据平台特性判断注册哪些插件
        /// </summary>
        /// <param name="kernel"></param>
        public static void ImportPluginFromObjectByOs(Kernel kernel)
        {

            //kernel.ImportPluginFromObject(new SystemPlugin(), "WindowsPlugin");
            //kernel.ImportPluginFromObject(new ChromePlugin(), "ChromePlugin");
            //kernel.ImportPluginFromObject(new ComputerPlugin(), "ComputerPlugin");
            string[] assemblies = ["Desktop.Assistant.Domain"];
            Type attributeType = typeof(OSAttribute);
            foreach (var item in assemblies)
            {
                Assembly assembly = Assembly.Load(item);

                var types = assembly.GetTypes();

                foreach (var classType in types)
                {
                    if (!classType.IsAbstract && classType.IsClass && classType.IsDefined(attributeType, false))
                    {
                       OSAttribute serviceAttribute = (classType.GetCustomAttribute(attributeType) as OSAttribute);
                        switch (serviceAttribute.OsType)
                        {
                            case OSType.All:
                                kernel.ImportPluginFromObject(Activator.CreateInstance(classType), classType.Name);
                                break;
                            case OSType.Windows:
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                {
                                    kernel.ImportPluginFromObject(Activator.CreateInstance(classType), classType.Name);
                                }
                                break;
                            case OSType.Linux:
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                                {
                                    kernel.ImportPluginFromObject(Activator.CreateInstance(classType), classType.Name);
                                }
                                break;
                            case OSType.OSX:
                                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                                {
                                    kernel.ImportPluginFromObject(Activator.CreateInstance(classType), classType.Name);
                                }
                                break;
                        }
                    }
                }
            }
        }
    }
}
