using Microsoft.SemanticKernel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InventoryAgent
{
    internal class InventoryPlugin
    {
        [KernelFunction]
        [Description("Searches the inventory database for a product and returns its price and stock level.")]
        public string GetProductInfo(string productName)
        {
            Console.WriteLine($"[InventoryPlugin] GetProductInfo called with productName: {productName}");
            //We will be reading from our inventory database to get the current stock price for the given stock name and return it as a string.
            // Use AppContext.BaseDirectory to find the folder where the .exe is running
            string _dbPath = Path.Combine(AppContext.BaseDirectory, "products.json");
          var jsonData= File.ReadAllText(_dbPath);
            var products = System.Text.Json.JsonSerializer.Deserialize<List<Product>>(jsonData);
            var item= products?.FirstOrDefault(p => p.Name.Equals(productName, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return $"Sorry, I couldn't find any information for the product '{productName}' in out database.";
            }
            string status = item.stock > 0 ? $"In Stock ({item.stock} left)" : "Out of Stock";
            return $"The {item.Name} costs ${item.Price}. Status: {status}.";
        }
    }
}

class Product()
{
    public int Id { get; set; }
    public string Name { get; set; }
    public decimal Price { get; set; }
    public int stock { get; set; }
}
