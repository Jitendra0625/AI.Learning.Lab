using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InventoryAgent
{
    internal class CalculatorPlugin
    {
        [KernelFunction]
        [Description("Adds two numbers together. Use this to calculate the total of multiple different items.")]
        public double Add(double a, double b)
        {
            Console.WriteLine($"[CalculatorPlugin] Add called with a: {a}, b: {b}");
            return a + b;
        }
        [KernelFunction]
        [Description("Multiplies two numbers. Use this for tax or bulk order calculations.")]
        public double Multiply(double a, double b)
        {
            return a * b;
        }
    }
}
