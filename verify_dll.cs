using System;
using System.Reflection;

class Program 
{
    static void Main(string[] args) 
    {
        try 
        {
            var assembly = Assembly.LoadFrom("MultiStratManagerRepo/External/NTGrpcClient/bin/Release/net48/NTGrpcClient.dll");
            var type = assembly.GetType("NTGrpcClient.TradingGrpcClient");
            
            Console.WriteLine("Methods in TradingGrpcClient:");
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                Console.WriteLine($"- {method.Name}({string.Join(", ", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name + " " + p.Name))}) : {method.ReturnType.Name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }
}