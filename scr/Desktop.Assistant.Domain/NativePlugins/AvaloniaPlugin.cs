using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Domain.NativePlugins
{
    [OS(OSType.All)]
    public class AvaloniaPlugin
    {
        [KernelFunction, Description("查询Avalonia官网")]
        public async Task<string> Search()
        {
            string result = "";
            try
            {
                // 目标地址
                string url = "https://docs.avaloniaui.net/zh-Hans/docs/welcome";

                HttpClient httpClient=new HttpClient();
                // 调用异步GET方法请求接口
                //异步示例
                HttpResponseMessage response = await httpClient.GetAsync(url).ConfigureAwait(false);

                // 确保响应成功，否则抛出异常
                response.EnsureSuccessStatusCode();

                // 读取响应内容
                result = await response.Content.ReadAsStringAsync();

            }
            catch (HttpRequestException e)
            {
              
            }
            return result;
        }
    }
}
