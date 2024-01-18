using Microsoft.SemanticKernel;
using Microsoft.Win32;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins
{
    
    [OS(OSType.Windows)] //插件导入平台，由于SK是根据类导入，所以这个特性要放在类上，本想放在方法上。
    public class ChromePlugin
    {

        [KernelFunction, Description("打开Chrome浏览器")]
        public string OpenChrome([Description("url地址")] string url)
        {


            if (!string.IsNullOrEmpty(url))
            {

                driver.Navigate().GoToUrl(url);


                return "打开成功！";
            }
            else
            {
                // Chrome未安装或找不到chrome.exe

                return "Chrome浏览器未找到！";
            }
        }

        [KernelFunction, Description("打开百度搜索")]
        public string OpenBaidu([Description("搜索关键字")] string key)
        {
            try
            {

                driver.Navigate().GoToUrl("http://www.baidu.com");
                // 找到元素
                IWebElement element = driver.FindElement(By.Id("kw"));

                // 例如，输入文本到一个文本框
                element.SendKeys(key);

                // 点击一个按钮
                IWebElement button = driver.FindElement(By.Id("su"));
                button.Click();

                Thread.Sleep(1000);

                IWebElement body = driver.FindElement(By.Id("content_left"));

                // 如果需要获取body的innerHTML，可以这样做：
                string bodyContent = body.GetAttribute("innerText");
                return bodyContent;

            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static IWebDriver driver = new ChromeDriver();
    }

  
}
