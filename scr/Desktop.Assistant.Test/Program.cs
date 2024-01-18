using OpenQA.Selenium.Chrome;
using OpenQA.Selenium;
using System;
using System.Diagnostics;
using System.Management;

using (IWebDriver driver = new ChromeDriver())
{
    // 打开网页
    driver.Navigate().GoToUrl("https://www.baidu.com/");

    // 找到元素
    IWebElement element = driver.FindElement(By.Id("kw"));

    // 例如，输入文本到一个文本框
    element.SendKeys("许泽宇真帅");

    // 点击一个按钮
    IWebElement button = driver.FindElement(By.Id("su"));
    button.Click();

    // 获取页面的标题
    string pageTitle = driver.Title;

    // 打印页面标题
    Console.WriteLine("Page Title: " + pageTitle);



    // 等待，以免结果太快退出
    Thread.Sleep(5000);

    // 关闭浏览器
    driver.Quit();
}
Console.WriteLine("执行完成");
Console.ReadKey();