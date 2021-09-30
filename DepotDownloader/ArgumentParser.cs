using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;

namespace DepotDownloader
{
    /// <summary>
    ///     Quick argument parser
    ///     Need some optimisation, but it is functional ;)
    /// </summary>
    public static class ArgumentParser
    {
        /// <summary>
        ///     Parse command line arguments and set Properties in TContainer object
        /// </summary>
        /// <param name="args">Command line arguments</param>
        /// <typeparam name="TContainer">Type implementing IArgumentContainer</typeparam>
        /// <returns>TContainer object with properties set</returns>
        /// <exception cref="ArgumentException"></exception>
        public static TContainer Parse<TContainer>(string[] args) where TContainer : IArgumentContainer
        {
            var containerType = typeof(TContainer);
            var container = (TContainer)Activator.CreateInstance(containerType);

            if (container == null)
            {
                throw new ArgumentException($"Type {containerType} has no empty constructor");
            }

            var options = GetContainerOptions(containerType);

            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("-"))
                {
                    throw new ArgumentException($"Unknown argument: {args[i]}");
                }

                var arg = args[i].StartsWith("--") ? args[i].Substring(2) : args[i].Substring(1);

                var (option, property) = (from op in options
                    where
                        arg.Length == 1 && op.Item1.ShortOption == arg[0]
                        || op.Item1.LongOption.Equals(arg, StringComparison.Ordinal)
                    select op).FirstOrDefault();

                if (option == null || property == null)
                {
                    throw new ArgumentException($"Unknown argument: '{arg}'");
                }

                if (option.ParameterName != null || option.AllowMultiple)
                {
                    if (i == args.Length - 1)
                    {
                        throw new ArgumentException($"No parameter for option '{arg}' found");
                    }

                    if (!option.AllowMultiple)
                    {
                        var parameter = args[++i];
                        if (parameter.StartsWith("-"))
                        {
                            throw new ArgumentException($"No parameter for option '{arg}' found");
                        }

                        property.SetValue(container,
                            property.PropertyType == typeof(string)
                                ? parameter
                                : TypeDescriptor.GetConverter(property.PropertyType).ConvertFromString(parameter));
                    }
                    else
                    {
                        var converter = property.PropertyType.IsGenericType
                            ? TypeDescriptor.GetConverter(property.PropertyType.GenericTypeArguments[0])
                            : null;

                        var list = (IList)property.GetValue(container);
                        if (list == null)
                        {
                            throw new ArgumentException("Initialize List properties first!");
                        }

                        while (i < args.Length && !args[i + 1].StartsWith("-"))
                        {
                            var parameter = converter != null ? converter.ConvertFromString(args[++i]) : args[++i];
                            list.Add(parameter);
                        }
                    }
                }
            }

            return container;
        }

        // TODO wrap
        /// <summary>
        ///     Creates a parameter table for given container type
        /// </summary>
        /// <param name="ident">number of whitespaces at the line beginning</param>
        /// <param name="wrap">wrap long descriptions (not implemented yet)</param>
        /// <typeparam name="T">Container type</typeparam>
        /// <returns></returns>
        public static string GetHelpList<T>(int ident = 4, bool wrap = false) where T : IArgumentContainer
        {
            var optionList = GetContainerOptions(typeof(T));
            var sb = new StringBuilder();

            var lines = new List<(string, string)>();
            var maxOpLength = 0;
            foreach (var (option, _) in optionList)
            {
                var opStr = option.ShortOption != '\0' ? $"-{option.ShortOption}" : "";
                if (!string.IsNullOrEmpty(option.LongOption))
                {
                    opStr += (option.ShortOption != '\0' ? ", " : "    ") + $"--{option.LongOption}";
                }

                if (option.ParameterName != null)
                {
                    opStr += $" <{option.ParameterName}>";
                }

                lines.Add((opStr, option.Description));
                maxOpLength = Math.Max(maxOpLength, opStr.Length);
            }

            var identStr = "".PadRight(ident);
            foreach (var (op, desc) in lines)
            {
                sb.AppendLine(identStr + op.PadRight(maxOpLength + 4) + desc);
            }

            return sb.ToString();
        }

        private static List<(OptionAttribute, PropertyInfo)> GetContainerOptions(Type containerType)
        {
            var resultList = new List<(OptionAttribute, PropertyInfo)>();

            foreach (var prop in containerType.GetProperties())
            {
                // try to get OptionAttribute from property
                var a = prop.GetCustomAttribute(typeof(OptionAttribute));

                if (a == null)
                {
                    continue;
                }

                // Check some things
                var option = (OptionAttribute)a;
                if (prop.SetMethod == null)
                {
                    throw new ArgumentException($"No setter found for '{prop.Name}'");
                }

                // Only options with descriptions are allowed!
                if (string.IsNullOrEmpty(option.Description))
                {
                    throw new ArgumentException($"No description found for '{prop.Name}'");
                }

                // We need a short option or a long option
                if (option.ShortOption == '\0' && string.IsNullOrEmpty(option.LongOption))
                {
                    throw new ArgumentException(
                        $"You must at least permit ShortOption or LongOption. Property: '{prop.Name}");
                }

                // AllowMultiple only allowed on list properties
                if (option.AllowMultiple && !prop.PropertyType.IsAssignableTo(typeof(IList)))
                {
                    throw new ArgumentException(
                        $"Options with AllowMultiple must be assignable to IList type. Property: '{prop.Name}");
                }

                if (!option.AllowMultiple && option.ParameterName == null && prop.PropertyType != typeof(bool))
                {
                    throw new ArgumentException(
                        $"Property must be bool if there is no parameter required. Property: '{prop.Name}");
                }

                // if everything is ok, add it to the result list
                resultList.Add((option, prop));
            }

            return resultList;
        }
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class OptionAttribute : Attribute
    {
        /// <summary>
        ///     Used for lists of parameters, seperated by whitespaces
        /// </summary>
        public bool AllowMultiple = false;

        /// <summary>
        /// </summary>
        public string Description = null;

        /// <summary>
        ///     long option name (e.g. --username)
        /// </summary>
        public string LongOption = null;

        /// <summary>
        ///     Name of parameter if parameter is needed (e.g. <user>)
        /// </summary>
        public string ParameterName = null;

        /// <summary>
        ///     single character option (e.g. -u)
        /// </summary>
        public char ShortOption = '\0';
    }

    public interface IArgumentContainer
    {
    }
}
