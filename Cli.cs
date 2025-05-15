using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ConsoleApp1
{
    public static class Cli
    {
        public static ICommand ParseCommandLine(String[] args, ICommand[] commands)
        {
            if (args == null)
                throw new ArgumentNullException("args");

            if (commands == null)
                throw new ArgumentNullException("commands");

            if (args.Length == 1 && (args[0] == "-h" || args[0] == "--help"))
            {
                var helpType = GetHelpTypeByArg0(args[0]);
                throw new ProgramHelpException(helpType);
            }

            if (args.Length == 1 && (args[0] == "-v" || args[0] == "--version"))
            {
                throw new PrintVersionException();
            }

            if (args.Length == 1 && (args[0] == "--print-app-settings"))
            {
                throw new PrintAppSettingsException();
            }

            if (args.Length == 0)
            {
                foreach (var command in commands)
                {
                    if (GetIfAttributeTypeIsPresent(command, typeof(DefaultCommandAttribute)))
                    {
                        ParseArgumentFields(new string[0], command);
                        return command;
                    }
                }
                throw new ProgramHelpException(HelpType.Quick);
            }

            string commandName = args[0];

            foreach (var command in commands)
            {
                if (command.CommandName == commandName)
                {
                    var commandArgs = args.Skip(1).ToArray();
                    if (commandArgs.Length >= 1 && (commandArgs[0] == "-h" || commandArgs[0] == "--help"))
                    {
                        var helpType = GetHelpTypeByArg0(commandArgs[0]);
                        throw new CommandHelpException(command, helpType);
                    }
                    ParseArgumentFields(commandArgs, command);
                    return command;
                }
            }

            throw new UnknownCommandException(L10n.Could_not_find_the_command(commandName));
        }

        public static void ParseCommandLine(String[] args, Object program)
        {
            if (program == null)
                throw new ArgumentNullException("obj");

            if (args.Length == 1 && (args[0] == "-h" || args[0] == "--help"))
            {
                var helpType = GetHelpTypeByArg0(args[0]);
                throw new ProgramHelpException(helpType);
            }

            if (args.Length == 1 && (args[0] == "-v" || args[0] == "--version"))
            {
                throw new PrintVersionException();
            }

            if (args.Length == 1 && (args[0] == "--print-app-settings"))
            {
                throw new PrintAppSettingsException();
            }

            ParseArgumentFields(args, program);
        }

        private static HelpType GetHelpTypeByArg0(string arg0)
            => arg0.CompareTo("-h") == 0 ? HelpType.Quick : HelpType.Full;

        public static void AskUserForInput(Object obj, string fieldName)
        {
            if (obj == null)
                throw new ArgumentNullException("obj");
            if (fieldName == null)
                throw new ArgumentNullException("fieldName");

            var allFields = GetArgumentFields(obj, ArgumentFieldTypes.Interactive);
            ArgumentField argumentField = GetArgumentFieldByFieldName(obj, allFields, fieldName);

            if (TryGetInteractiveInputLabel(
                argumentField.Info, out string inputLabel, out bool useDeaultIfEmpty))
            {
                bool userInputFinished = false;
                do
                {
                    StringBuilder argumentDoc = new StringBuilder();
                    argumentDoc.AppendLine($"{inputLabel}:");
                    AppendArgumentDocumentation(argumentField, obj, argumentDoc, true);
                    if (useDeaultIfEmpty)
                        argumentDoc.AppendLine(L10n.just_press_enter_to_use_default_value());
                    argumentDoc.AppendLine(L10n.press_Ctrl_C_to_interrupt());
                    Console.Write(argumentDoc.ToString());

                    Console.Write("> ");
                    try
                    {
                        string userInput = string.Empty;
                        if (argumentField.IsSecret)
                            userInput = ConsoleReadLine.ReadSecret();
                        else
                            userInput = Console.ReadLine();

                        if (!string.IsNullOrEmpty(userInput))
                        {
                            ValidateAndSetNewArgumentValue(obj, argumentField, userInput);
                            CommitAllNewValues(obj, new ArgumentField[] { argumentField });
                            userInputFinished = true;
                            break;
                        }
                        else
                        {
                            if (useDeaultIfEmpty)
                                break;
                        }
                    }
                    catch (IOException) // Ctrl-C pressed
                    {
                        throw new UserInterruptedInputException();
                    }
                    catch (ArgumentParseException e)
                    {
                        Console.WriteLine($"{e.Message}");
                        Console.WriteLine(L10n.Please_retry());
                    }

                } while (!userInputFinished);

            }
            else
                throw new InvalidOperationException(L10n.The_field_does_not_support_interactive_input(fieldName));
        }

        public static bool AskUserIfYesOrNo(
            string inputLabel, string yes = "Yes", string no = "No")
        {
            if (string.IsNullOrEmpty(inputLabel))
                throw new InvalidOperationException(L10n.The_argument_must_not_be_null_or_empty_string(nameof(inputLabel)));
            if (string.IsNullOrEmpty(yes))
                throw new InvalidOperationException(L10n.The_argument_must_not_be_null_or_empty_string(nameof(yes)));
            if (string.IsNullOrEmpty(no))
                throw new InvalidOperationException(L10n.The_argument_must_not_be_null_or_empty_string(nameof(no)));

            do
            {
                StringBuilder argumentDoc = new StringBuilder();
                argumentDoc.AppendLine($"{inputLabel} [{yes}/{no}]");
                Console.Write(argumentDoc.ToString());
                Console.Write("> ");
                try
                {
                    string userInput = Console.ReadLine();
                    string simplified = userInput.ToLower().TrimStart();
                    if (simplified.Length == 0)
                        continue;
                    if (no.ToLower().StartsWith(simplified))
                    {
                        return false;
                    }
                    if (yes.ToLower().StartsWith(simplified))
                    {
                        return true;
                    }
                }
                catch (IOException) // Ctrl-C pressed
                {
                    throw new UserInterruptedInputException();
                }
                catch (ArgumentParseException e)
                {
                    Console.WriteLine($"{e.Message}");
                    Console.WriteLine(L10n.Please_retry());
                }
            } while (true);
        }

        public static void AskIfUserWantedContinue(
            string inputLabel = "Would you like to continue?",
            string yes = "Yes",
            string no = "No")
        {
            var result = AskUserIfYesOrNo(inputLabel, yes, no);
            if (!result)
                throw new UserInterruptedInputException(L10n.User_decided_to_discontinue_the_process());
        }

        public static void ChangeValue(Object obj, string fieldName, string strNewValue)
        {
            if (obj == null)
                throw new ArgumentNullException("obj");
            if (fieldName == null)
                throw new ArgumentNullException("fieldName");
            if (strNewValue == null)
                throw new ArgumentNullException("strNewValue");

            var allFields = GetArgumentFields(obj, ArgumentFieldTypes.All);
            ArgumentField argumentField = GetArgumentFieldByFieldName(obj, allFields, fieldName);
            ValidateAndSetNewArgumentValue(obj, argumentField, strNewValue);
            CommitAllNewValues(obj, new ArgumentField[] { argumentField });
        }

        public static string ArgumentLongName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName))
                throw new ArgumentException(L10n.The_argument_must_not_be_null_or_empty_string(fieldName));
            return PascalCaseToLispCase(fieldName);
        }

        private static void ValidateAndSetNewArgumentValue(Object obj, ArgumentField argumentField, string strNewValue)
        {
            // apply the validations if possible
            ApplyAllValidationsIfPossible(obj, argumentField, strNewValue);
            argumentField.NewValue =
                ConvertArgumentStringToTargetType(strNewValue, obj, argumentField.Info.FieldType);
        }




        private static void ParseArgumentFields(String[] args, Object obj)
        {
            if (args == null)
                throw new ArgumentNullException("args");

            if (obj == null)
                throw new ArgumentNullException("obj");


            var allFields =
                GetArgumentFields(obj, ArgumentFieldTypes.ForCommandLineParsing);

            // read arguments from appSettings
            ReadArgumentsFromAppSettings(obj, allFields, true);
            // read arguments from environment variables
            ReadArgumentsFromEnvironmentVariables(obj, allFields, true);
            // commit everything to the target
            CommitAllNewValues(obj, allFields);

            // re-read arguments but only named and positional
            allFields =
                GetArgumentFields(obj, ArgumentFieldTypes.NamedOrPositional);

            int positionalArgumentIndex = 0;
            for (int index = 0; index < args.Length; index++)
            {
                var arg = args[index];

                string strArgValue = string.Empty;
                ArgumentField argumentField;

                // named arguments
                if (arg.StartsWith("-"))
                {
                    if (arg.Length == 2 && arg.StartsWith("-") && Char.IsLetterOrDigit(arg[1]))
                    {
                        var argName = arg[1];
                        argumentField = GetArgumentFieldByShortName(obj, allFields, argName.ToString());
                    }
                    else if (arg.Length > 2 && arg.StartsWith("--"))
                    {
                        var argName = arg.Substring(2);
                        argumentField = GetArgumentFieldByLongName(obj, allFields, argName);
                    }
                    else
                    {
                        throw new ArgumentParseException(
                            L10n.Could_not_find_named_parameter_arg_to_assign_the_argument_value_at_the_argument_position_index(arg, index), obj);

                    }

                    // determine the parameter value as string
                    if (!argumentField.IsFlag) // assume the positive value for any boolean fields if they are present among the arguments
                    {
                        if (index >= (args.Length - 1))
                        {
                            throw new ArgumentParseException(
                                L10n.The_named_parameter_arg_must_have_an_argument(arg), obj);
                        }
                        strArgValue = args[index + 1];
                        index++;
                    }
                }
                else
                {
                    argumentField = GetArgumentFieldByIndex(obj, allFields, positionalArgumentIndex++);
                    strArgValue = args[index];
                }

                if (argumentField == null)
                    throw new ArgumentParseException(
                        L10n.Could_not_find_parameter_to_assign_the_argument_value_arg_at_the_argument_position_positionalArgumentIndex(arg, positionalArgumentIndex), obj);

                if (argumentField.IsRestOfArguments && argumentField.Info.FieldType.IsArray)
                {
                    string[] elements = args.Skip(index).ToArray();
                    ValidateRangeOfArrayElements(obj, argumentField, elements);
                    ValidatePatternOfArrayElements(obj, argumentField, elements);
                    Type elementType = argumentField.Info.FieldType.GetElementType();
                    argumentField.NewValue =
                        ConvertArgumentStringsToArrayOfType(elements, obj, elementType);
                    break;
                }
                else
                {
                    ApplyAllValidationsIfPossible(obj, argumentField, strArgValue);
                    // assign the value                                                                                                           
                    if (argumentField.IsFlag)
                    {
                        argumentField.NewValue = true;
                    }
                    else
                    {
                        argumentField.NewValue =
                            ConvertArgumentStringToTargetType(strArgValue, obj, argumentField.Info.FieldType);
                    }
                }
            }

            // cross check
            foreach (var field in allFields)
            {
                // make sure that all of the fields with required flag have got new value
                if (field.IsRequired && field.NewValue == null)
                {
                    if (field.IsNamed)
                        throw new ArgumentParseException(L10n.The_argument_name_must_be_provided(field.ArgumentName), obj);
                    else if (field.IsPositional)
                        throw new ArgumentParseException(L10n.The_argument_with_index_must_be_provided(field.ArgumentName, field.PositionIndex + 1), obj);
                    else // interactive and app settings only
                        throw new ArgumentParseException(L10n.The_argument_name_must_be_provided(field.ArgumentName), obj);
                }
            }

            // assign all new non-null values
            CommitAllNewValues(obj, allFields);
        }

        private static void ApplyAllValidationsIfPossible(Object obj, ArgumentField argumentField, string strArgValue)
        {
            // apply the validations if possible
            if (argumentField.Info.FieldType.IsArray)
            {
                string[] elements = SplitToArrayElements(strArgValue);
                ValidateRangeOfArrayElements(obj, argumentField, elements);
                ValidatePatternOfArrayElements(obj, argumentField, elements);
            }
            else
            {
                ValidateRangeOfSingleObject(obj, argumentField, strArgValue);
                ValidatePatternOfSinleObject(obj, argumentField, strArgValue);
            }
        }

        private static void ReadArgumentsFromEnvironmentVariables(Object obj, IEnumerable<ArgumentField> argumentFields, bool rethrowArgumentParseException)
        {
            // read arguments from appSettings
            foreach (var argumentField in argumentFields)
            {
                if (argumentField.IsEnvironmentVar)
                {
                    var strArgValue = Environment.GetEnvironmentVariable(argumentField.EnvironmentVar);
                    if (strArgValue != null)
                    {
                        // apply the validations if possible
                        try
                        {
                            ApplyAllValidationsIfPossible(obj, argumentField, strArgValue);
                            argumentField.NewValue =
                                ConvertArgumentStringToTargetType(strArgValue, obj, argumentField.Info.FieldType);
                        }
                        catch (ArgumentParseException ex)
                        {
                            if (rethrowArgumentParseException)
                                throw new ArgumentParseException(L10n.Could_not_parse_value_of_environment_variable(argumentField.EnvironmentVar, ex.Message), obj, ex);
                            else
                                Console.Error.WriteLine(L10n.Could_not_parse_value_of_environment_variable(argumentField.EnvironmentVar, ex.Message));
                        }
                    }
                }
            }
        }

        private static void ReadArgumentsFromAppSettings(Object obj, IEnumerable<ArgumentField> argumentFields, bool rethrowArgumentParseException)
        {
            // read arguments from appSettings
            foreach (var argumentField in argumentFields)
            {
                if (argumentField.IsAppSettings || argumentField.IsNamed || argumentField.IsPositional || argumentField.IsInteractive)
                {
                    if (TryGetValueFromAppSettings(
                        obj, argumentField.ArgumentName, argumentField.Info.FieldType.IsArray, out string strArgValue))
                    {
                        // apply the validations if possible
                        try
                        {
                            ApplyAllValidationsIfPossible(obj, argumentField, strArgValue);
                            argumentField.NewValue =
                                ConvertArgumentStringToTargetType(strArgValue, obj, argumentField.Info.FieldType);
                        }
                        catch (ArgumentParseException ex)
                        {
                            if (rethrowArgumentParseException)
                                throw;
                            else
                                Console.Error.WriteLine(L10n.Error_during_parsing_parameter_ArgumentName_in_appSettings_section_of_app_config(argumentField.ArgumentName, ex.Message));
                        }
                    }
                }
            }
        }

        private static void CommitAllNewValues(Object obj, IEnumerable<ArgumentField> argumentFields)
        {
            // assign all new non-null values
            foreach (var argumentField in argumentFields)
            {
                if (argumentField.NewValue != null)
                    argumentField.Info.SetValue(obj, argumentField.NewValue);
            }
        }

        private static object ConvertArgumentStringToTargetType(string strArgValue, Object obj, Type targetType)
        {
            if (targetType.IsArray)
            {
                Type elementType = targetType.GetElementType();
                string[] elements = SplitToArrayElements(strArgValue);
                var result = ConvertArgumentStringsToArrayOfType(elements, obj, elementType);
                return result;
            }
            else
            {
                var resut = ConvertArgumentStringToSingleObject(strArgValue, obj, targetType);
                return resut;
            }
        }

        private static bool TryGetValueFromAppSettings(Object obj, string argumentName, bool isArray, out string argumentValue)
        {
#pragma warning disable CS0618
            if (obj == null)
                throw new ArgumentNullException("obj");

            if (obj is ICommand)
            {
                var commandName = ((ICommand)obj).CommandName;
                var key1 = $"Cli.{commandName}.{argumentName}";
                if (TryGetValueFromAppSettingsByKey(obj, key1, isArray, out argumentValue))
                    return true;
                var key2 = $"Cli.*.{argumentName}";
                if (TryGetValueFromAppSettingsByKey(obj, key2, isArray, out argumentValue))
                    return true;
            }
            else
            {
                var key3 = $"Cli.{argumentName}";
                if (TryGetValueFromAppSettingsByKey(obj, key3, isArray, out argumentValue))
                    return true;
            }
            return false;

        }

        private static bool TryGetValueFromAppSettingsByKey(Object obj, string baseKey, bool isArray, out string argumentValue)
        {
#pragma warning disable CS0618
            if (isArray)
            {
                const int MaxArrayIndex = 100;
                bool hasFoundAnyValue = false;
                var valBuilder = new StringBuilder();

                // firstly check for base key
                var arrVal = System.Configuration.ConfigurationSettings.AppSettings[baseKey];
                if (arrVal != null)
                {
                    valBuilder.Append(arrVal);
                    hasFoundAnyValue = true;
                }
                // next check for item keys
                for (int i = 1; i < MaxArrayIndex; i++)
                {
                    var itemKey = $"{baseKey}.{i}";
                    var itemVal = System.Configuration.ConfigurationSettings.AppSettings[itemKey];
                    if (itemVal != null)
                    {
                        hasFoundAnyValue = true;
                        var withReplacement = itemVal.Replace("\\", "\\\\").Replace(",", "\\,");
                        if (valBuilder.Length > 0)
                            valBuilder.Append($",{withReplacement}");
                        else
                            valBuilder.Append($"{withReplacement}");
                    }
                    else
                        break;
                }
                if (hasFoundAnyValue)
                {
                    argumentValue = valBuilder.ToString();
                    argumentValue = ApplyAllAppConfigXRefs(obj, argumentValue);
                    return true;
                }
            }
            else
            {
                argumentValue = System.Configuration.ConfigurationSettings.AppSettings[baseKey];
                if (argumentValue != null)
                {
                    argumentValue = ApplyAllAppConfigXRefs(obj, argumentValue);
                    return true;
                }
            }
            argumentValue = null;
            return false;
        }

        private static string ApplyAllAppConfigXRefs(Object obj, string str)
        {
#pragma warning disable CS0618
            string s1 = str;
            var hasFoundAny = TryFindAppConfigXRefPlaceholder(s1, out string xrefName, out int xrefStart, out int xrefLen);
            while (hasFoundAny)
            {
                s1 = s1.Remove(xrefStart, xrefLen);                
                var value = System.Configuration.ConfigurationSettings.AppSettings[xrefName];
                if (value != null)
                {
                    s1 = s1.Insert(xrefStart, value);
                }
                else
                    throw new ArgumentParseException(L10n.Could_not_find_referenced_key_xrefName_in_appSettings_section_of_app_config(xrefName),obj);
                hasFoundAny = TryFindAppConfigXRefPlaceholder(s1, out xrefName, out xrefStart, out xrefLen);
            }
            return s1;
        }

        // [[<cross reference to app settings key>]]
        private static bool TryFindAppConfigXRefPlaceholder(string str, out string configXRefName, out int startChar, out int length)
        {
            const string StartTag = "[[";
            const string EndTag = "]]";
            int startTagIndex = str.IndexOf(StartTag, 0, StringComparison.OrdinalIgnoreCase);
            if (startTagIndex > 0)
            {
                int endTagIndex = str.IndexOf(EndTag, startTagIndex + StartTag.Length);
                if (endTagIndex > 0)
                {
                    int nameStart = startTagIndex + StartTag.Length;
                    int lengthOfEnvName = endTagIndex - nameStart;
                    configXRefName = str.Substring(nameStart, lengthOfEnvName);
                    startChar = startTagIndex;
                    length = endTagIndex + EndTag.Length - startTagIndex;
                    return true;
                }

            }
            configXRefName = string.Empty;
            startChar = 0;
            length = 0;
            return false;
        }

        private static object ConvertArgumentStringToSingleObject(string strArgValue, Object obj, Type targetType)
        {
            try
            {
                object converted;
                if (targetType == typeof(string))
                {
                    converted = strArgValue;
                }
                else if (targetType.IsEnum)
                {
                    converted = Enum.Parse(targetType, strArgValue, true);
                }
                else if (targetType == typeof(DateTime))
                {
                    converted = DateTime.Parse(
                        strArgValue, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal);
                }
                else if (targetType == typeof(DateTimeOffset))
                {
                    converted = DateTimeOffset.Parse(
                        strArgValue, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal);
                }
                else
                {
                    converted = Convert.ChangeType(strArgValue, targetType);
                }
                return converted;
            }
            catch (Exception ex)
            {
                throw new ArgumentParseException(L10n.Could_not_cast_string_to_the_target_type(strArgValue, targetType.Name), obj, ex);
            }
        }

        private static object ConvertArgumentStringsToArrayOfType(string[] elements, Object obj, Type elementType)
        {
            if (elementType == typeof(string))
            {
                return elements;
            }
            else
            {
                Array result = Array.CreateInstance(elementType, elements.Length);
                for (int i = 0; i < elements.Length; i++)
                {
                    var element = elements[i];
                    try
                    {
                        var converted = ConvertArgumentStringToSingleObject(element, obj, elementType);
                        result.SetValue(converted, i);
                    }
                    catch (Exception ex)
                    {
                        throw new ArgumentParseException(L10n.Could_not_cast_element_number_i_with_value_equals_element_to_the_target_type(i, element, elementType.Name), obj, ex);
                    }
                }
                return result;
            }
        }


        internal class ArgumentField
        {
            public FieldInfo Info;
            public string ArgumentName;
            public bool IsNamed;
            public string ShortName;
            public string LongName;
            public bool IsPositional;
            public int PositionIndex;
            public bool IsAppSettings;
            public bool IsInteractive;
            public bool IsFlag;
            public bool IsRequired;
            public bool IsRestOfArguments;
            public bool HasSampleValue;
            public Object SampleValue;
            public bool IsSecret;
            public bool IsEnvironmentVar;
            public string EnvironmentVar;
            public Object NewValue;
        }

        public enum ArgumentFieldTypes {
            NamedOrPositional = 1, AppSettings = 2, Interactive = 4, EnvironmentVar = 8,
            ForCommandLineParsing = NamedOrPositional | AppSettings | EnvironmentVar, All = NamedOrPositional | AppSettings | Interactive | EnvironmentVar}
        private static List<ArgumentField> GetArgumentFields(
            Object obj,
            ArgumentFieldTypes opts = ArgumentFieldTypes.ForCommandLineParsing)
        {
            if (obj == null)
                throw new ArgumentNullException("obj");

            var result = new List<ArgumentField>();
            int nextPositionalArgumentIndex = 0;
            foreach (FieldInfo fieldInfo in obj.GetType().GetFields())
            {
                var argumentField = new ArgumentField();
                bool shouldBeAdded = false;
                if ((opts & ArgumentFieldTypes.NamedOrPositional) > 0)
                {
                    argumentField.IsNamed =
                        TryGetArgumentNames(fieldInfo, out argumentField.ShortName, out argumentField.LongName, out argumentField.IsFlag);
                    if (!argumentField.IsNamed)
                    {
                        argumentField.IsPositional =
                            TryGetArgumentPosition(fieldInfo, out argumentField.PositionIndex, ref nextPositionalArgumentIndex);
                    }
                    if (argumentField.IsNamed || argumentField.IsPositional)
                        shouldBeAdded = true;
                }
                if ((opts & ArgumentFieldTypes.AppSettings) > 0)
                {
                    argumentField.IsAppSettings =
                        GetIfAttributeTypeIsPresent(fieldInfo, typeof(AppSettingsAttribute));
                    if (argumentField.IsAppSettings)
                        shouldBeAdded = true;
                }
                if ((opts & ArgumentFieldTypes.EnvironmentVar) > 0)
                {
                    argumentField.IsEnvironmentVar =
                        TryGetArgumentEnvironmentVariable(fieldInfo, out argumentField.EnvironmentVar);
                    if (argumentField.IsEnvironmentVar)
                        shouldBeAdded = true;
                    if (string.IsNullOrEmpty(argumentField.EnvironmentVar))
                        argumentField.EnvironmentVar = PascalCaseToScreamingSnakeCase(fieldInfo.Name);
                }
                if ((opts & ArgumentFieldTypes.Interactive) > 0)
                {
                    argumentField.IsInteractive =
                        GetIfAttributeTypeIsPresent(fieldInfo, typeof(InteractiveAttribute));
                    if (argumentField.IsInteractive)
                        shouldBeAdded = true;
                }
                if (shouldBeAdded)
                {
                    argumentField.Info = fieldInfo;
                    argumentField.ArgumentName = argumentField.IsNamed ? argumentField.LongName : PascalCaseToLispCase(fieldInfo.Name);
                    argumentField.IsRequired =
                        GetIfAttributeTypeIsPresent(fieldInfo, typeof(RequiredAttribute));
                    argumentField.IsRestOfArguments =
                        GetIfAttributeTypeIsPresent(fieldInfo, typeof(RestOfArgumentsAttribute));
                    argumentField.HasSampleValue =
                        TryGetArgumentSampleValue(fieldInfo, out argumentField.SampleValue);
                    argumentField.IsSecret =
                        GetIfAttributeTypeIsPresent(fieldInfo, typeof(SecretAttribute));
                    argumentField.NewValue = null;
                    result.Add(argumentField);
                }
            }
            return result;
        }

        private static ArgumentField GetArgumentFieldByIndex(Object obj, List<ArgumentField> fields, int index)
        {
            foreach (var field in fields)
            {
                if (field.IsPositional && field.PositionIndex == index)
                {
                    return field;
                }
            }
            throw new ArgumentParseException(L10n.Could_not_find_positional_parameter_by_index_It_might_be_too_much_arguments(index), obj);
        }

        private static ArgumentField GetArgumentFieldByShortName(Object obj, List<ArgumentField> fields, string shortName)
        {
            foreach (var field in fields)
            {
                if (field.IsNamed && (field.ShortName == shortName))
                {
                    return field;
                }
            }
            throw new ArgumentParseException(L10n.Could_not_find_named_parameter_by_short_name_It_might_be_too_much_arguments(shortName), obj);
        }
        private static ArgumentField GetArgumentFieldByLongName(Object obj, List<ArgumentField> fields, string longName)
        {
            foreach (var field in fields)
            {
                if (field.IsNamed && (field.LongName == longName))
                {
                    return field;
                }
            }
            throw new ArgumentParseException(L10n.Could_not_find_named_parameter_by_long_name_It_might_be_too_much_arguments(longName), obj);
        }

        private static ArgumentField GetArgumentFieldByFieldName(Object obj, List<ArgumentField> fields, string name)
        {
            foreach (var field in fields)
            {
                if (field.Info.Name == name)
                {
                    return field;
                }
            }
            throw new ArgumentParseException(L10n.Could_not_find_field_by_name_It_might_be_something_wrong(name), obj);
        }

        public static void PrintVersion()
        {
            var version = GetEntryAssemblyVersion();
            Console.WriteLine(version);
        }

        public static void PrintCommandLine(string[] args)
        {
            if (args == null)
                throw new ArgumentNullException("obj");

            var programName = System.AppDomain.CurrentDomain.FriendlyName;

            StringBuilder commandLine = new StringBuilder();
            commandLine.AppendLine(L10n.Command_line_provided());
            commandLine.Append($"{PlatformDependentCommandLineInvitation} {programName} ");
            foreach (var arg in args)
            {
                commandLine.Append($"{arg} ");
            }
            Console.WriteLine(commandLine.ToString());
        }

        public enum HelpType { 
            ShortNamedAndPositionalArguments = 1, 
            LongNamedArguments = 2, 
            EnvironmentVariables = 8, 
            Quick = ShortNamedAndPositionalArguments,
            Full = ShortNamedAndPositionalArguments | LongNamedArguments | EnvironmentVariables
        };

        public static void PrintUsage(Object programObj, HelpType helpType = HelpType.Full)
        {
            if (programObj == null)
                throw new ArgumentNullException("obj");

            var programName = System.AppDomain.CurrentDomain.FriendlyName;

            var argumentFields = GetArgumentFields(programObj, ArgumentFieldTypes.ForCommandLineParsing);
            try
            {
                ReadArgumentsFromAppSettings(programObj, argumentFields, false);
                ReadArgumentsFromEnvironmentVariables(programObj, argumentFields, true);
                CommitAllNewValues(programObj, argumentFields);
            }
            catch (ArgumentParseException)
            {
                // TODO: inform user that some value from appsettings are not readable
            }

            StringBuilder commandLineUsage = new StringBuilder();
            commandLineUsage.Append(L10n.Usage_program(PlatformDependentCommandLineInvitation, programName));

            StringBuilder argumentDocumentation = new StringBuilder();
            foreach (var argumentField in argumentFields)
            {

                if (((helpType & HelpType.ShortNamedAndPositionalArguments) > 0)
                    && IsArgumentFieldForShortNamedAndPositionalHelp(argumentField))
                {
                    AppendArgumentUsageToCommandLine(argumentField, commandLineUsage);
                    AppendArgumentDocumentation(argumentField, programObj, argumentDocumentation);
                }
                else if (((helpType & HelpType.LongNamedArguments) > 0)
                    && IsArgumentFieldForLongNamedHelp(argumentField))
                {
                    AppendArgumentUsageToCommandLine(argumentField, commandLineUsage);
                    AppendArgumentDocumentation(argumentField, programObj, argumentDocumentation);
                }
                else if (((helpType & HelpType.EnvironmentVariables) > 0)
                    && IsArgumentFieldForEnvironmentVarsHelp(argumentField))
                {
                    AppendArgumentUsageToCommandLine(argumentField, commandLineUsage);
                    AppendArgumentDocumentation(argumentField, programObj, argumentDocumentation);
                }

            }

            commandLineUsage.AppendLine();
            AppendCommandDocumentation(programName, null, programObj, commandLineUsage);

            if (TryGetGenerateSampleAttributeWithTitle(programObj, out string sampleTitle))
            {
                AppendCommandSample(programName, null, programObj, argumentFields, sampleTitle, commandLineUsage);
                commandLineUsage.AppendLine();
            }

            Console.WriteLine(commandLineUsage.ToString());
            Console.WriteLine(argumentDocumentation.ToString());

        }

        public static void PrintUsage(Object programObj, ICommand[] commands, HelpType helpType = HelpType.Full)
        {
            if (commands == null)
                throw new ArgumentNullException("commands");

            var programName = System.AppDomain.CurrentDomain.FriendlyName;

            StringBuilder commandDocumentation = new StringBuilder();
            foreach (var command in commands)
            {
                AppendCommandDocumentation(command, helpType, commandDocumentation);
                commandDocumentation.AppendLine();
            }
            StringBuilder commandLineUsage = new StringBuilder();
            AppendCommandDocumentation(programName, null, programObj, commandLineUsage);
            commandLineUsage.AppendLine(L10n.Usage_program_with_commands(PlatformDependentCommandLineInvitation, programName));
            commandLineUsage.AppendLine();
            commandLineUsage.AppendLine(L10n.Available_commands());

            Console.WriteLine(commandLineUsage.ToString());
            Console.WriteLine(commandDocumentation.ToString());
        }

        public static void PrintCommandUsage(ICommand cmd, HelpType helpType = HelpType.Full)
        {
            if (cmd == null)
                throw new ArgumentNullException("cmd");

            var programName = System.AppDomain.CurrentDomain.FriendlyName;
            var commandName = cmd.CommandName;

            var argumentFields = GetArgumentFields(cmd, ArgumentFieldTypes.ForCommandLineParsing | ArgumentFieldTypes.Interactive);
            try
            {
                ReadArgumentsFromAppSettings(cmd, argumentFields, false);
                ReadArgumentsFromEnvironmentVariables(cmd, argumentFields, false);
                CommitAllNewValues(cmd, argumentFields);
            }
            catch (ArgumentParseException)
            {
            }

            StringBuilder commandLineUsage = new StringBuilder();
            commandLineUsage.Append(L10n.Usage_command(PlatformDependentCommandLineInvitation, programName, commandName));
            StringBuilder argumentDocumentation = new StringBuilder();
            StringBuilder envVarsDocumentation = new StringBuilder();
            foreach (var argumentField in argumentFields)
            {
                if (argumentField.IsNamed || argumentField.IsPositional)
                {
                    if (((helpType & HelpType.ShortNamedAndPositionalArguments) > 0)
                        && IsArgumentFieldForShortNamedAndPositionalHelp(argumentField))
                    {
                        AppendArgumentUsageToCommandLine(argumentField, commandLineUsage);
                        AppendArgumentDocumentation(argumentField, cmd, argumentDocumentation);
                    }
                    else if (((helpType & HelpType.LongNamedArguments) > 0)
                        && IsArgumentFieldForLongNamedHelp(argumentField))
                    {
                        AppendArgumentUsageToCommandLine(argumentField, commandLineUsage);
                        AppendArgumentDocumentation(argumentField, cmd, argumentDocumentation);
                    }
                    else if (((helpType & HelpType.EnvironmentVariables) > 0)
                        && IsArgumentFieldForEnvironmentVarsHelp(argumentField))
                    {
                        AppendArgumentUsageToCommandLine(argumentField, commandLineUsage);
                        AppendArgumentDocumentation(argumentField, cmd, argumentDocumentation);
                    }
                }

                if (((helpType & HelpType.EnvironmentVariables) > 0)
                        && IsArgumentFieldForEnvironmentVarsHelp(argumentField))
                {
                    if (TryGetArgumentDocumentation(argumentField.Info, out var documentation))
                        envVarsDocumentation.AppendLine($"#   - {documentation}");
                    AppendArgumentDocumentation(argumentField, cmd, envVarsDocumentation, true, "#");
                    var envVar = argumentField.EnvironmentVar;
                    var val = argumentField.Info.GetValue(cmd);
                    var str = StringValueOfSinleObjectOrArray(val, argumentField.IsSecret, argumentField.IsRestOfArguments);
                    envVarsDocumentation.AppendLine($"export {envVar}=\"{str}\";");

                }
            }
            commandLineUsage.AppendLine();
            AppendCommandDocumentation(programName, commandName, cmd, commandLineUsage);

            if (TryGetGenerateSampleAttributeWithTitle(cmd, out string sampleTitle))
            {
                AppendCommandSample(programName, commandName, cmd, argumentFields, sampleTitle, commandLineUsage);
            }

            Console.WriteLine(commandLineUsage.ToString());
            Console.WriteLine(argumentDocumentation.ToString());

            if (envVarsDocumentation.Length > 0)
            {
                Console.WriteLine(L10n.All_allowed_environment_variables());
                Console.WriteLine(envVarsDocumentation.ToString());
            }

        }

        public static void PrintAppSettings(Object programObj, ICommand[] commands = null)
        {
            if (programObj == null)
                throw new ArgumentNullException("programObj");

            StringBuilder appSettingsBuilder = new StringBuilder();

            appSettingsBuilder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" ?>");
            appSettingsBuilder.AppendLine("<configuration>");
            appSettingsBuilder.AppendLine(" <appSettings>");

            AppendCommandAppSettings(programObj, appSettingsBuilder);
            if (commands != null)
            {
                foreach (var command in commands)
                {
                    AppendCommandAppSettings(command, appSettingsBuilder);
                }
            }
            appSettingsBuilder.AppendLine(" </appSettings>");
            appSettingsBuilder.AppendLine("</configuration>");

            Console.WriteLine(appSettingsBuilder.ToString());
        }

        public static void AppendCommandAppSettings(Object cmd, StringBuilder appSettingsDocumentation)
        {
            if (cmd == null)
                throw new ArgumentNullException(nameof(cmd));

            var commandName = cmd is Cli.ICommand ? (cmd as Cli.ICommand).CommandName : string.Empty;

            if (!string.IsNullOrEmpty(commandName))
            {
                appSettingsDocumentation.AppendLine($"  <!-- {L10n.CommandName_command_settings(commandName)} -->");
            }
            else
            {
                appSettingsDocumentation.AppendLine($"  <!-- {L10n.Program_settings()} -->");
            }
            appSettingsDocumentation.AppendLine();

            var argumentFields = GetArgumentFields(cmd, ArgumentFieldTypes.All);

            try
            {
                ReadArgumentsFromAppSettings(cmd, argumentFields, false);
                CommitAllNewValues(cmd, argumentFields);
            }
            catch (ArgumentParseException)
            {
            }

            foreach (var argumentField in argumentFields)
            {
                if (IsArgumentFieldForAppSettingsKeysHelp(argumentField))
                {
                    appSettingsDocumentation.AppendLine($"  <!--");
                    if (TryGetArgumentDocumentation(argumentField.Info, out var documentation))
                        appSettingsDocumentation.AppendLine($"   - {documentation}");
                    AppendArgumentDocumentation(argumentField, cmd, appSettingsDocumentation, true);
                    appSettingsDocumentation.AppendLine($"  -->");
                    var val = argumentField.Info.GetValue(cmd);
                    var str = StringValueOfSinleObjectOrArray(val, argumentField.IsSecret, argumentField.IsRestOfArguments);
                    var xmlEscaped = EscapeXmlSpecialSymbols(str);
                    if (!string.IsNullOrEmpty(commandName))
                        appSettingsDocumentation.AppendLine($"  <add key=\"Cli.{commandName}.{argumentField.ArgumentName}\"");
                    else
                        appSettingsDocumentation.AppendLine($"  <add key=\"Cli.{argumentField.ArgumentName}\"");
                    appSettingsDocumentation.AppendLine($"       value=\"{xmlEscaped}\"/>");
                }
            }
            appSettingsDocumentation.AppendLine();
        }


        private static string EscapeXmlSpecialSymbols(string str)
        {
            string [] symbols = {
                "&", "\"", "'", "<", ">",  };
            string[] xmlTags = {
                "&amp;", "&quot;", "&apos;", "&lt;", "&gt;", };

            for (int i = 0; i < symbols.Length; i++)
            {
                string c = symbols[i];
                string tag = xmlTags[i];
                str = str.Replace(c, tag);
            }
            return str;
        }

        private static string PlatformDependentCommandLineInvitation =>
                System.Environment.OSVersion.Platform == PlatformID.Unix ? "$" : ">";
        private static void AppendCommandDocumentation(ICommand command, HelpType helpType, StringBuilder builder)
        {
            var programName = System.AppDomain.CurrentDomain.FriendlyName;
            var commandName = command.CommandName;
            var isDefault = GetIfAttributeTypeIsPresent(command, typeof(DefaultCommandAttribute));
            builder.Append($"  {(isDefault ? "*" : "")}{commandName} [-h|--help] ");

            foreach (var argumentField in GetArgumentFields(command, ArgumentFieldTypes.NamedOrPositional))
            {
                if (((helpType & HelpType.ShortNamedAndPositionalArguments) > 0)
                    && IsArgumentFieldForShortNamedAndPositionalHelp(argumentField))
                {
                    AppendArgumentUsageToCommandLine(argumentField, builder);
                }
                else if (((helpType & HelpType.LongNamedArguments) > 0) 
                    && IsArgumentFieldForLongNamedHelp(argumentField))
                {
                    AppendArgumentUsageToCommandLine(argumentField, builder);
                }
                else if (((helpType & HelpType.EnvironmentVariables) > 0)
                    && IsArgumentFieldForEnvironmentVarsHelp(argumentField))
                {
                    AppendArgumentUsageToCommandLine(argumentField, builder);
                }

            }
            builder.AppendLine();
            AppendCommandDocumentation(programName, commandName, command, builder);
        }

        private static bool IsArgumentFieldForShortNamedAndPositionalHelp(ArgumentField argumentField)
            => ((argumentField.IsNamed && !string.IsNullOrEmpty(argumentField.ShortName)) || argumentField.IsPositional);
        private static bool IsArgumentFieldForLongNamedHelp(ArgumentField argumentField)
            => (argumentField.IsNamed && string.IsNullOrEmpty(argumentField.ShortName));
        private static bool IsArgumentFieldForAppSettingsKeysHelp(ArgumentField argumentField)
            => ((argumentField.IsNamed || argumentField.IsPositional || argumentField.IsAppSettings) && !argumentField.IsRequired);
        private static bool IsArgumentFieldForEnvironmentVarsHelp(ArgumentField argumentField)
            => (argumentField.IsEnvironmentVar && !argumentField.IsRequired);

        private static void AppendArgumentUsageToCommandLine(ArgumentField argumentField, StringBuilder commandLineBuilder)
        {
            if (argumentField.IsNamed)
            {
                if (argumentField.IsFlag)
                {
                    if (String.IsNullOrEmpty(argumentField.ShortName))
                    {
                        AppendArgumentWithRequiredFlag($"--{argumentField.LongName}", argumentField.IsRequired, commandLineBuilder);
                    }
                    else
                    {
                        AppendArgumentWithRequiredFlag($"-{argumentField.ShortName}|--{argumentField.LongName}", argumentField.IsRequired, commandLineBuilder);
                    }
                }
                else
                {
                    var lastWordOfName = LastWordOfPhraseInPascalCase(argumentField.Info.Name);
                    if (String.IsNullOrEmpty(argumentField.ShortName))
                    {
                        AppendArgumentWithRequiredFlag($"--{argumentField.LongName} {lastWordOfName}", argumentField.IsRequired, commandLineBuilder);
                    }
                    else
                    {
                        AppendArgumentWithRequiredFlag($"-{argumentField.ShortName}|--{argumentField.LongName} {lastWordOfName}", argumentField.IsRequired, commandLineBuilder);
                    }
                }
            }
            else
            {
                if (argumentField.IsRestOfArguments)
                {
                    AppendArgumentWithRestOfArgumentsFlag($"{argumentField.ArgumentName}", argumentField.IsRequired, commandLineBuilder);
                }
                else
                {
                    AppendArgumentWithRequiredFlag($"{argumentField.ArgumentName}", argumentField.IsRequired, commandLineBuilder);
                }
            }
        }

        private static void AppendArgumentDocumentation(
            ArgumentField argumentField, Object obj, StringBuilder builder, bool skipArgumentNameAndDocLines = false, string docLinePrefix = "")
        {
            if (!skipArgumentNameAndDocLines)
            {
                builder.Append(" ");
                AppendArgumentUsageToCommandLine(argumentField, builder);
                builder.AppendLine();
                if (TryGetArgumentDocumentation(argumentField.Info, out var documentation))
                {
                    builder.AppendLine($"{docLinePrefix}   - {documentation}");
                }
            }

            if (argumentField.Info.FieldType.IsEnum)
            {
                string possibleValues = EnumeratePossibleValues(argumentField.Info.FieldType);
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.the_data_type_is_enum_with_possible_values(argumentField.Info.FieldType.Name, possibleValues));
            }
            else if (argumentField.Info.FieldType.IsArray)
            {
                Type elementType = argumentField.Info.FieldType.GetElementType();
                if (elementType.IsEnum)
                {
                    string possibleValues = EnumeratePossibleValues(elementType);
                    builder.Append(docLinePrefix);
                    builder.AppendLine(L10n.the_array_of_enums_with_possible_values(argumentField.Info.FieldType.Name, possibleValues));
                }
                else
                {
                    builder.Append(docLinePrefix);
                    builder.AppendLine(L10n.the_data_type_is_(argumentField.Info.FieldType.Name));
                }
            }
            else
            {
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.the_data_type_is_(argumentField.Info.FieldType.Name));
            }

            if (TryGetArgumentValidationRange(argumentField.Info, out int minVal, out int maxVal))
            {
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.allowed_value_range_is_between_minVal_and_maxVal(MinRangeBoundaryText(minVal), MaxRangeBoundaryText(maxVal)));
            }

            if (TryGetArgumentValidationPattern(argumentField.Info, out string pattern, out bool ignoreCase))
            {
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.allowed_regex_pattern_is_pattern(pattern));
            }

            if (TryGetArgumentSampleValue(argumentField.Info, out object sampleValue))
            {
                var str = StringValueOfSinleObjectOrArray(sampleValue, argumentField.IsSecret, argumentField.IsRestOfArguments);
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.sample_value_is(str));
            }

            if (argumentField.IsRequired)
            {
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.is_required_to_provide());
            }
            else
            {
                var val = argumentField.Info.GetValue(obj);
                var str = StringValueOfSinleObjectOrArray(val, argumentField.IsSecret, argumentField.IsRestOfArguments);
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.default_value_is(str));
            }

            if (argumentField.IsRestOfArguments)
            {
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.includes_rest_of_commandline_arguments());
            }

            if (argumentField.IsEnvironmentVar)
            {
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.Could_be_passed_via_environment_variable_envVar(argumentField.EnvironmentVar));
            }

            if (argumentField.IsSecret)
            {
                builder.Append(docLinePrefix);
                builder.AppendLine(L10n.The_value_is_a_secret());
            }
        }

        private static string MinRangeBoundaryText(int boundary)
        {
            return (boundary == Int32.MinValue) ? "int32.MinValue" : Convert.ToString(boundary);
        }
        private static string MaxRangeBoundaryText(int boundary)
        {
            return (boundary == Int32.MaxValue) ? "int32.MaxValue" : Convert.ToString(boundary);
        }

        private static string StringValueOfSinleObjectOrArray(object obj, bool isSecret, bool isRestOfArguments)
        {
            if (obj == null)
                return "null";

            bool isArray = obj.GetType().IsArray;
            if (isSecret)
            {
                if (!isArray)
                {
                    return "***";
                }
                else
                {
                    if (isRestOfArguments)
                    {
                        return "\"***\" \"***\"";
                    }
                    else
                    {
                        return "***,***";
                    }
                }
            }
            else
            {
                if (!isArray)
                {
                    return Convert.ToString(obj);
                }
                else
                {
                    Array array = obj as Array;
                    if (array == null)
                        return String.Empty; // something got wrong
                    bool firstTime = true;
                    var result = new StringBuilder();
                    foreach (var element in array)
                    {
                        if (firstTime)
                        {
                            if (isRestOfArguments)
                                result.Append($" \"{element}\"");
                            else
                                result.Append($"{element}");
                            firstTime = false;
                        }
                        else
                        {
                            if (isRestOfArguments)
                                result.Append($" \"{element}\"");
                            else
                                result.Append($",{element}");
                        }

                    }
                    return result.ToString();
                }
            }
        }

        private static string EnumeratePossibleValues(Type enumType)
        {
            var result = new StringBuilder();
            bool firstTime = true;
            foreach (var name in enumType.GetEnumNames())
            {
                if (firstTime)
                {
                    result.Append($"{name}");
                    firstTime = false;
                }
                else
                {
                    result.Append($",{name}");
                }
            }
            return result.ToString();
        }

        private static void AppendArgumentWithRequiredFlag(string str, bool isRequired, StringBuilder builder)
        {
            if (isRequired)
            {
                builder.Append($"<{str}> ");
            }
            else
            {
                builder.Append($"[{str}] ");
            }
        }

        private static void AppendArgumentWithRestOfArgumentsFlag(string str, bool isRequired, StringBuilder builder)
        {
            if (isRequired)
            {
                builder.Append($"<{str}1> [{str}2] ... [{str}N]");
            }
            else
            {
                builder.Append($"[{str}1] [{str}2] ... [{str}N] ");
            }
        }

        private static void AppendCommandDocumentation(string programName, string commandName, Object obj, StringBuilder builder, string docLinePrefix = "   ")
        {
            const string ProgramPlaceholder = "[[program]]";
            const string CommandPlaceholder = "[[command]]";
            const string VersionPlaceholder = "[[version]]";


            if (obj == null)
                throw new ArgumentNullException("obj");

            foreach (var attr in obj.GetType().CustomAttributes)
            {
                if (attr.AttributeType == typeof(DocAttribute))
                {
                    foreach (var constructorArgument in attr.ConstructorArguments)
                    {
                        if (constructorArgument.ArgumentType == typeof(String) && constructorArgument.Value != null)
                        {
                            var docLine = constructorArgument.Value.ToString();

                            if (!String.IsNullOrEmpty(programName))
                            {
                                docLine = docLine.Replace(ProgramPlaceholder, programName);
                            }
                            if (!String.IsNullOrEmpty(commandName))
                            {
                                docLine = docLine.Replace(CommandPlaceholder, commandName);
                            }
                            if (docLine.Contains(VersionPlaceholder))
                            {
                                string assemblyVersion = GetEntryAssemblyVersion();
                                docLine = docLine.Replace(VersionPlaceholder, assemblyVersion);
                            }

                            builder.AppendLine($"{docLinePrefix}- {docLine}");
                        }
                    }
                }
            }
        }

        private static string GetEntryAssemblyVersion()
        {
            var version = Assembly.GetEntryAssembly().GetName().Version;
            return version != null ? version.ToString() : "0.0.0.0";
        }

        private static String PascalCaseToLispCase(String str)
        {
            // Pascal case to lisp case
            StringBuilder builder = new StringBuilder();
            foreach (Char c in str)
            {
                if (Char.IsUpper(c))
                {
                    if (builder.Length > 0)
                        builder.Append('-');
                    builder.Append(Char.ToLower(c));
                }
                else
                {
                    builder.Append(c);
                }
            }
            var result = builder.ToString();
            return result;
        }
        private static String PascalCaseToScreamingSnakeCase(String str)
        {
            // Pascal case to SCREAMING_SNAKE_CASE
            StringBuilder builder = new StringBuilder();
            foreach (Char c in str)
            {
                if (Char.IsUpper(c))
                {
                    if (builder.Length > 0)
                        builder.Append('_');
                    builder.Append(c);
                }
                else
                {
                    builder.Append(Char.ToUpper(c));
                }
            }
            var result = builder.ToString();
            return result;
        }
        private static String LastWordOfPhraseInPascalCase(String str)
        {
            // last word from phrase in pascal case
            int lastCapitalCharacter = 0;
            for (int i = 0; i < str.Length; i++)
            {
                char c = str[i];
                if (Char.IsUpper(c))
                    lastCapitalCharacter = i;
            }
            var result = str.Substring(lastCapitalCharacter, str.Length - lastCapitalCharacter).ToLower();
            return result;
        }

        [FlagsAttribute]
        public enum PrintArgOpts {
            ShowDocs = 1, ShowNames = 2, ShowDataTypes = 4, ShowHeader = 8, ShowAll = ShowDocs | ShowNames | ShowDataTypes | ShowHeader };

        public static void PrintArgs(
            Object obj,
            PrintArgOpts opts = PrintArgOpts.ShowAll,
            ArgumentFieldTypes fieldTypes = ArgumentFieldTypes.All)
        {
            if (obj == null)
                throw new ArgumentNullException("obj");

            StringBuilder builder = new StringBuilder();
            if ((opts & PrintArgOpts.ShowHeader) > 0)
                builder.AppendLine(L10n.Running_with_arguments());
            foreach (var argumentField in GetArgumentFields(obj, fieldTypes))
            {
                var val = argumentField.Info.GetValue(obj);
                var str = StringValueOfSinleObjectOrArray(val, argumentField.IsSecret, argumentField.IsRestOfArguments);
                var argName = argumentField.ArgumentName;

                if (TryGetArgumentDocumentation(argumentField.Info, out var doc))
                {
                    if ((opts & PrintArgOpts.ShowDocs) > 0)
                    {
                        if ((opts & PrintArgOpts.ShowNames) > 0)
                        {
                            builder.AppendLine($"   {doc} / {argName}");
                        }
                        else
                        {
                            builder.AppendLine($"   {doc}");
                        }
                        if ((opts & PrintArgOpts.ShowDataTypes) > 0)
                        {
                            builder.AppendLine($"      = {str} [{argumentField.Info.FieldType.Name.ToLower()}]");
                        }
                        else
                        {
                            builder.AppendLine($"      = {str}");
                        }
                    }
                    else if ((opts & PrintArgOpts.ShowNames) > 0)
                    {
                        if ((opts & PrintArgOpts.ShowDataTypes) > 0)
                        {
                            builder.AppendLine($"      {argName} = {str} [{argumentField.Info.FieldType.Name.ToLower()}]");
                        }
                        else
                        {
                            builder.AppendLine($"      {argName} = {str}");
                        }
                    }
                }
                else
                {
                    if ((opts & PrintArgOpts.ShowNames) > 0)
                    {
                        if ((opts & PrintArgOpts.ShowDataTypes) > 0)
                        {
                            builder.AppendLine($"      {argName} = {str} [{argumentField.Info.FieldType.Name.ToLower()}]");
                        }
                        else
                        {
                            builder.AppendLine($"      {argName} = {str}");
                        }
                    }
                }
            }
            Console.WriteLine(builder.ToString());
        }

        private static void AppendCommandSample(
            string programName, string commandName, Object obj, IEnumerable<ArgumentField> argumentFields, string sampleTitle, StringBuilder builder)
        {
            if (obj == null)
                throw new ArgumentNullException(nameof(obj));
            if (argumentFields == null)
                throw new ArgumentNullException(nameof(argumentFields));
            if (builder == null)
                throw new ArgumentNullException(nameof(builder));

            builder.Append(L10n.Sample_colon());
            builder.Append($"{PlatformDependentCommandLineInvitation} {programName}");
            if (!string.IsNullOrEmpty(commandName))
                builder.Append($" {commandName}");

            foreach (ArgumentField f in argumentFields)
            {
                if (f.IsPositional)
                {
                    var val = f.HasSampleValue ? f.SampleValue : f.Info.GetValue(obj);
                    var str = StringValueOfSinleObjectOrArray(val, f.IsSecret, f.IsRestOfArguments);
                    if (f.IsRestOfArguments)
                        builder.Append($" {str}");
                    else
                        builder.Append($" \"{str}\"");
                }
                else if (f.IsNamed && (f.IsRequired || f.HasSampleValue))
                {
                    var val = f.HasSampleValue ? f.SampleValue : f.Info.GetValue(obj);
                    var str = StringValueOfSinleObjectOrArray(val, f.IsSecret, false);
                    if (!string.IsNullOrEmpty(f.ShortName))
                    {
                        builder.Append($" -{f.ShortName} \"{str}\"");
                    }
                    else
                    {
                        builder.Append($" --{f.LongName} \"{str}\"");
                    }
                }
            }
            builder.AppendLine();
            if (!string.IsNullOrEmpty(sampleTitle))
                builder.AppendLine($"   - {sampleTitle}");
        }

        private static bool TryGetArgumentDocumentation(FieldInfo fieldInfo, out string doc)
        {
            doc = string.Empty;
            foreach (var attr in fieldInfo.CustomAttributes)
            {
                if (attr.AttributeType == typeof(DocAttribute))
                {
                    var constuctorArgs = attr.ConstructorArguments.GetEnumerator();
                    if (constuctorArgs.MoveNext())
                    {
                        var arg = constuctorArgs.Current;
                        if (arg.ArgumentType == typeof(String))
                        {
                            doc = Convert.ToString(arg.Value);
                            return true;
                        }
                    }
                }
            }
            return false;

        }

        private static bool TryGetArgumentNames(FieldInfo fieldInfo, out string shortName, out string longName, out bool isFlag)
        {
            shortName = string.Empty; longName = string.Empty; isFlag = false;
            foreach (var attr in fieldInfo.CustomAttributes)
            {
                if (attr.AttributeType == typeof(NamedAttribute))
                {
                    foreach (var constructorArgument in attr.ConstructorArguments)
                    {
                        if (constructorArgument.ArgumentType == typeof(String))
                        {
                            var constArgValue = constructorArgument.Value;
                            if (constArgValue != null)
                            {
                                longName = constArgValue.ToString();
                            }
                        }
                        if (constructorArgument.ArgumentType == typeof(Char))
                        {
                            var constArgValue = constructorArgument.Value;
                            if (constArgValue != null)
                            {
                                shortName = constArgValue.ToString();
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(longName))
                    {
                        longName = PascalCaseToLispCase(fieldInfo.Name);
                    }
                    isFlag = fieldInfo.FieldType == typeof(Boolean);
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetArgumentPosition(FieldInfo fieldInfo, out int positionIndex, ref int nextPositionalFieldIndex)
        {
            positionIndex = -1;
            foreach (var attr in fieldInfo.CustomAttributes)
            {

                if (attr.AttributeType == typeof(PositionalAttribute))
                {
                    positionIndex = nextPositionalFieldIndex++;
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetArgumentValidationRange(FieldInfo fieldInfo, out int min, out int max)
        {
            min = Int32.MinValue; max = Int32.MaxValue;
            foreach (var attr in fieldInfo.CustomAttributes)
            {
                if (attr.AttributeType == typeof(AllowedRangeAttribute))
                {
                    var constuctorArgs = attr.ConstructorArguments.GetEnumerator();
                    if (constuctorArgs.MoveNext())
                    {
                        var arg = constuctorArgs.Current;
                        if (arg.ArgumentType == typeof(Int32))
                        {
                            min = Convert.ToInt32(arg.Value);
                        }
                    }
                    if (constuctorArgs.MoveNext())
                    {
                        var arg = constuctorArgs.Current;
                        if (arg.ArgumentType == typeof(Int32))
                        {
                            max = Convert.ToInt32(arg.Value);
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool GetIfAttributeTypeIsPresent(FieldInfo fieldInfo, Type attributeType)
        {
            foreach (var attr in fieldInfo.CustomAttributes)
            {

                if (attr.AttributeType == attributeType)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetInteractiveInputLabel(FieldInfo fieldInfo, out string inputLabel, out bool useDeaultIfEmpty)
        {
            inputLabel = null;
            useDeaultIfEmpty = false;
            foreach (var attr in fieldInfo.CustomAttributes)
            {
                if (attr.AttributeType == typeof(InteractiveAttribute))
                {
                    var constuctorArgs = attr.ConstructorArguments.GetEnumerator();
                    if (constuctorArgs.MoveNext())
                    {
                        var arg0 = constuctorArgs.Current;
                        if (arg0.ArgumentType == typeof(string))
                        {
                            inputLabel = arg0.Value.ToString();
                            if (constuctorArgs.MoveNext())
                            {
                                var arg1 = constuctorArgs.Current;
                                if (arg1.ArgumentType == typeof(bool))
                                {
                                    useDeaultIfEmpty = Convert.ToBoolean(arg1.Value);
                                    return true;
                                }
                            }
                        }
                    }
                    return false;
                }
            }
            return false;
        }

        private static bool TryGetArgumentSampleValue(FieldInfo fieldInfo, out Object value)
        {
            value = null;
            foreach (var attr in fieldInfo.CustomAttributes)
            {
                if (attr.AttributeType == typeof(SampleValueAttribute))
                {
                    var enumerator = attr.ConstructorArguments.GetEnumerator();
                    if (enumerator.MoveNext())
                    {
                        var arg0 = enumerator.Current;
                        value = arg0.Value;
                        return true;
                    }
                    return false;
                }
            }
            return false;
        }

        private static bool GetIfAttributeTypeIsPresent(Object programOrCommand, Type attributeType)
        {
            foreach (var attr in programOrCommand.GetType().CustomAttributes)
            {
                if (attr.AttributeType == attributeType)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetGenerateSampleAttributeWithTitle(Object programOrCommand, out string title)
        {
            title = null;
            foreach (var attr in programOrCommand.GetType().CustomAttributes)
            {
                if (attr.AttributeType == typeof(GenerateSampleAttribute))
                {
                    var enumerator = attr.ConstructorArguments.GetEnumerator();
                    if (enumerator.MoveNext())
                    {
                        var arg0 = enumerator.Current;
                        title = arg0.Value.ToString();
                    }
                    return true;
                }
            }
            return false;
        }

        private static void ValidateRangeOfSingleObject(Object obj, ArgumentField argumentField, string strNewValue)
        {
            if (!TryGetArgumentValidationRange(argumentField.Info, out int minVal, out int maxVal))
                return;

            // apply the validation if possible
            if (!Int32.TryParse(strNewValue, out int intVal))
                throw new ArgumentParseException(
                    L10n.Could_not_cast_argument_value_to_the_Int32_type_for_range_validation(strNewValue), obj);

            if (intVal < minVal)
                throw new ArgumentParseException(
                    L10n.The_value_of_argument_must_be_in_range_between_minVal_and_maxVal(
                        intVal, argumentField.ArgumentName, MinRangeBoundaryText(minVal), MaxRangeBoundaryText(maxVal)), obj);

            if (intVal > maxVal)
                throw new ArgumentParseException(
                    L10n.The_value_of_argument_must_be_in_range_between_minVal_and_maxVal(
                        intVal, argumentField.ArgumentName, MinRangeBoundaryText(minVal), MaxRangeBoundaryText(maxVal)), obj);
        }

        private static void ValidateRangeOfArrayElements(Object obj, ArgumentField argumentField, string[] elements)
        {
            if (!TryGetArgumentValidationRange(argumentField.Info, out int minVal, out int maxVal))
                return;

            Type elementType = typeof(Int32);
            for (int i = 0; i < elements.Length; i++)
            {
                string element = elements[i];
                if (!Int32.TryParse(element, out int intVal))
                    throw new ArgumentParseException(L10n.The_argument_validation_error_could_not_parse_array_element_number_i_with_value_equals_element_to_the_type(
                        argumentField.ArgumentName, i, element, elementType.Name), obj);

                //argument X validation error, value of array element number 5 with value 6 must be in the range from 1 to 10
                if (intVal < minVal)
                    throw new ArgumentParseException(
                        L10n.The_argument_validation_error_value_of_array_element_number_i_must_be_in_range_between_minVal_and_maxVal(
                            argumentField.ArgumentName, intVal, i, MinRangeBoundaryText(minVal), MaxRangeBoundaryText(maxVal)), obj);

                if (intVal > maxVal)
                    throw new ArgumentParseException(
                        L10n.The_argument_validation_error_value_of_array_element_number_i_must_be_in_range_between_minVal_and_maxVal(
                            argumentField.ArgumentName, intVal, i, MinRangeBoundaryText(minVal), MaxRangeBoundaryText(maxVal)), obj);
            }
        }

        private static string[] SplitToArrayElements(string str)
        {
            const char EscapeCharacter = '\\';
            const char ElemementSeparator = ',';

            var resultList = new List<string>();
            var elementBuilder = new StringBuilder();
            var strEnumerator = str.GetEnumerator();
            while (strEnumerator.MoveNext())
            {
                char c = strEnumerator.Current;
                if (c == EscapeCharacter)
                {
                    if (strEnumerator.MoveNext()) // skip escape character
                        elementBuilder.Append(c);
                }
                else if (c == ElemementSeparator)
                {
                    resultList.Add(elementBuilder.ToString());
                    elementBuilder.Clear();
                }
                else
                {
                    elementBuilder.Append(c);
                }
            }
            if (elementBuilder.Length > 0)
                resultList.Add(elementBuilder.ToString());
            return resultList.ToArray();
        }
        private static bool TryGetArgumentEnvironmentVariable(FieldInfo fieldInfo, out string envVar)
        {
            envVar = string.Empty; 
            foreach (var attr in fieldInfo.CustomAttributes)
            {
                if (attr.AttributeType == typeof(EnvironmentVariableAttribute))
                {
                    var constuctorArgs = attr.ConstructorArguments.GetEnumerator();
                    if (constuctorArgs.MoveNext())
                    {
                        var arg = constuctorArgs.Current;
                        if (arg.ArgumentType == typeof(String))
                        {
                            envVar = Convert.ToString(arg.Value);
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        private static bool TryGetArgumentValidationPattern(FieldInfo fieldInfo, out string pattern, out bool ignoreCase)
        {
            pattern = string.Empty; ignoreCase = false;
            foreach (var attr in fieldInfo.CustomAttributes)
            {
                if (attr.AttributeType == typeof(AllowedRegexPatternAttribute))
                {
                    var constuctorArgs = attr.ConstructorArguments.GetEnumerator();
                    if (constuctorArgs.MoveNext())
                    {
                        var arg = constuctorArgs.Current;
                        if (arg.ArgumentType == typeof(String))
                        {
                            pattern = Convert.ToString(arg.Value);
                        }
                    }
                    if (constuctorArgs.MoveNext())
                    {
                        var arg = constuctorArgs.Current;
                        if (arg.ArgumentType == typeof(Boolean))
                        {
                            ignoreCase = Convert.ToBoolean(arg.Value);
                        }
                    }
                    return true;
                }
            }
            return false;
        }

        private static void ValidatePatternOfSinleObject(Object obj, ArgumentField argumentField, string strNewValue)
        {
            if (!TryGetArgumentValidationPattern(argumentField.Info, out string pattern, out bool ignoreCase))
                return;

            // apply the validation if possible
            Regex regex = null;
            try
            {
                regex = new Regex(pattern,
                    ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            }
            catch (Exception ex)
            {
                throw new ArgumentParseException(L10n.The_value_of_argument_could_not_be_verfied_with_pattern_due_to_inner_error(strNewValue, argumentField.ArgumentName, pattern), obj, ex);
            }
            if (!regex.IsMatch(strNewValue))
                throw new ArgumentParseException(L10n.The_value_of_argument_does_not_match_the_pattern(strNewValue, argumentField.ArgumentName, pattern), obj);

        }
        private static void ValidatePatternOfArrayElements(Object obj, ArgumentField argumentField, string[] elements)
        {
            if (!TryGetArgumentValidationPattern(argumentField.Info, out string pattern, out bool ignoreCase))
                return;
            // apply the validation if possible
            Regex regex = null;
            try
            {
                regex = new Regex(pattern,
                    ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            }
            catch (Exception ex)
            {
                throw new ArgumentParseException(
                    L10n.The_argument_validation_error_validation_pattern_could_not_be_compiled(argumentField.ArgumentName, pattern), obj, ex);
            }

            for (int i = 0; i < elements.Length; i++)
            {
                string element = elements[i];
                if (!regex.IsMatch(element))
                    throw new ArgumentParseException(
                        L10n.The_argument_validation_error_Value_of_array_element_number_i_does_not_match_the_regex_pattern_pattern(
                            argumentField.ArgumentName, element, i, pattern), obj);
            }
        }

        private static ILocalizedLiterals L10n
        {
            get
            {
                if (literals == null)
                {
                    var currentCulture = CultureInfo.CurrentCulture.Name;
                    if (string.IsNullOrEmpty(currentCulture))
                        literals = new EnLiterals();
                    else if (currentCulture.StartsWith("ru"))
                        literals = new RuLiterals();
                    else if (currentCulture.StartsWith("en"))
                        literals = new EnLiterals();
                }
                return literals;
            }
        }
        private static ILocalizedLiterals literals = null;

        private interface ILocalizedLiterals
        {
            string Could_not_find_named_parameter_arg_to_assign_the_argument_value_at_the_argument_position_index(string arg, int index);
            string The_named_parameter_arg_must_have_an_argument(string arg);
            string Could_not_find_parameter_to_assign_the_argument_value_arg_at_the_argument_position_positionalArgumentIndex(string arg, int index);
            string The_argument_name_must_be_provided(string argumentName);
            string The_argument_with_index_must_be_provided(string argumentName, int index);
            string Could_not_cast_string_to_the_target_type(string strArgValue, string typeName);
            string Could_not_cast_element_number_i_with_value_equals_element_to_the_target_type(int i, string element, string type);
            string Could_not_find_positional_parameter_by_index_It_might_be_too_much_arguments(int index);
            string Could_not_find_named_parameter_by_short_name_It_might_be_too_much_arguments(string shortName);
            string Could_not_find_named_parameter_by_long_name_It_might_be_too_much_arguments(string longName);
            string Could_not_find_field_by_name_It_might_be_something_wrong(string name);
            string Could_not_cast_argument_value_to_the_Int32_type_for_range_validation(string value);
            string The_value_of_argument_must_be_in_range_between_minVal_and_maxVal(int intVal, string argumentName, string minVal, string maxVal);
            string The_argument_validation_error_could_not_parse_array_element_number_i_with_value_equals_element_to_the_type(string argumentName, int i, string element, string typeName);
            string The_argument_validation_error_value_of_array_element_number_i_must_be_in_range_between_minVal_and_maxVal(string argumentName, int intVal, int i, string minVal, string maxVal);
            string The_value_of_argument_could_not_be_verfied_with_pattern_due_to_inner_error(string strNewValue, string argumentName, string pattern);
            string The_value_of_argument_does_not_match_the_pattern(string strNewValue, string argumentName, string pattern);
            string The_argument_validation_error_validation_pattern_could_not_be_compiled(string argumentName, string pattern);
            string The_argument_validation_error_Value_of_array_element_number_i_does_not_match_the_regex_pattern_pattern(string argumentName, string element, int I, string pattern);
            string Could_not_find_the_command(string commandName);
            string The_field_does_not_support_interactive_input(string fieldName);
            string The_argument_must_not_be_null_or_empty_string(string argumentName);
            string just_press_enter_to_use_default_value();
            string press_Ctrl_C_to_interrupt();
            string Command_line_provided();
            string Usage_program(string platformDependentCommandLineInvitation, string programName);
            string Usage_program_with_commands(string platformDependentCommandLineInvitation, string programName);
            string Usage_command(string platformDependentCommandLineInvitation, string programName, string commandName);
            string Available_commands();
            string the_data_type_is_enum_with_possible_values(string typeName, string possibleValues);
            string the_array_of_enums_with_possible_values(string typeName, string possibleValues);
            string the_data_type_is_(string typeName);
            string allowed_value_range_is_between_minVal_and_maxVal(string minVal, string maxVal);
            string allowed_regex_pattern_is_pattern(string patten);
            string is_required_to_provide();
            string default_value_is(string str);
            string sample_value_is(string str);
            string includes_rest_of_commandline_arguments();
            string Please_retry();
            string User_decided_to_discontinue_the_process();
            string Running_with_arguments();
            string Sample_colon();
            string All_allowed_keys_within_appSettings_of_app_config_file(string programName);
            string Error_during_parsing_parameter_ArgumentName_in_appSettings_section_of_app_config(string argumentName, string message);
            string Could_not_find_referenced_key_xrefName_in_appSettings_section_of_app_config(string xrefName);
            string Could_not_parse_value_of_environment_variable(string envVar, string message);
            string All_allowed_environment_variables();
            string Could_be_passed_via_environment_variable_envVar(string envVar);
            string The_value_is_a_secret();
            string CommandName_command_settings(string commandName);
            string Program_settings();
        }


        private class EnLiterals : ILocalizedLiterals
        {
            public string The_argument_validation_error_validation_pattern_could_not_be_compiled(string argumentName, string pattern)
                => $"The argument \"{argumentName}\" validation error, validation pattern \"{pattern}\" could not be compiled";
            public string Could_not_find_named_parameter_arg_to_assign_the_argument_value_at_the_argument_position_index(string arg, int index)
                => $"Could not find named parameter '{arg}' to assign the argument value at the argument position '{index}'";
            public string The_named_parameter_arg_must_have_an_argument(string arg)
                => $"The named parameter '{arg}' must have an argument";
            public string Could_not_find_parameter_to_assign_the_argument_value_arg_at_the_argument_position_positionalArgumentIndex(string arg, int index)
                => $"Could not find parameter to assign the argument value '{arg}' at the argument position '{index}'";
            public string The_argument_name_must_be_provided(string argumentName)
                => $"The argument \"{argumentName}\" must be provided";
            public string The_argument_with_index_must_be_provided(string argumentName, int index)
                => $"The argument \"{argumentName}\" with index \"{index}\" must be provided";
            public string Could_not_cast_string_to_the_target_type(string strArgValue, string typeName)
                => $"Could not cast string \"{strArgValue}\" to the target type \"{typeName}\"";
            public string Could_not_cast_element_number_i_with_value_equals_element_to_the_target_type(int i, string element, string typeName)
                => $"Could not cast element number \"{i}\" with value = \"{element}\" to the target type \"{typeName}\"";
            public string Could_not_find_positional_parameter_by_index_It_might_be_too_much_arguments(int index)
                => $"Could not find positional parameter by index {index}, it might be too much arguments?";
            public string Could_not_find_named_parameter_by_short_name_It_might_be_too_much_arguments(string shortName)
                => $"Could not find named parameter by name \"-{shortName}\", it might be too much arguments?";
            public string Could_not_find_named_parameter_by_long_name_It_might_be_too_much_arguments(string longName)
                => $"Could not find named parameter by name \"--{longName}\", it might be too much arguments?";
            public string Could_not_find_field_by_name_It_might_be_something_wrong(string name)
                => $"Could not find field by name \"{name}\", it might be something went wrong?";
            public string Could_not_cast_argument_value_to_the_Int32_type_for_range_validation(string value)
                => $"Could not cast argument value \"{value}\" to the Int32 type for range validation";
            public string The_value_of_argument_must_be_in_range_between_minVal_and_maxVal(int intVal, string argumentName, string minVal, string maxVal)
                => $"The value {intVal} of argument \"{argumentName}\" must be in range between {minVal} and {maxVal} ";
            public string The_argument_validation_error_could_not_parse_array_element_number_i_with_value_equals_element_to_the_type(string argumentName, int i, string element, string typeName)
                => $"The argument \"{argumentName}\" validation error, could not parse array element number {i + 1} with value = \"{element}\" to the {typeName} type";
            public string The_argument_validation_error_value_of_array_element_number_i_must_be_in_range_between_minVal_and_maxVal(string argumentName, int intVal, int i, string minVal, string maxVal)
                => $"The argument \"{argumentName}\" validation error, value {intVal} of array element number {i + 1} must be in range between {minVal} and {maxVal} ";
            public string The_value_of_argument_could_not_be_verfied_with_pattern_due_to_inner_error(string strNewValue, string argumentName, string pattern)
                => $"The value \"{strNewValue}\" of argument \"{argumentName}\" could not be verified with pattern \"{pattern}\" due to inner error ";
            public string The_value_of_argument_does_not_match_the_pattern(string strNewValue, string argumentName, string pattern)
                => $"The value \"{strNewValue}\" of argument \"{argumentName}\" does not match the pattern \"{pattern}\" ";
            public string The_argument_validation_error_Value_of_array_element_number_i_does_not_match_the_regex_pattern_pattern(string argumentName, string element, int i, string pattern)
                => $"The argument \"{argumentName}\" validation error, value \"{element}\" of array element number {i + 1} does not match the regex pattern \"{pattern}\" ";
            public string Could_not_find_the_command(string commandName)
                => $"Could not find the command: {commandName}";
            public string Command_line_provided()
                => "Command line provided:";
            public string The_field_does_not_support_interactive_input(string fieldName)
                => $"The field \"{fieldName}\" does not support interactive input!";
            public string The_argument_must_not_be_null_or_empty_string(string argumentName)
                => $"The argument \"{argumentName}\" must not be null or empty string";
            public string just_press_enter_to_use_default_value()
                => $"   - just press enter to use default value";
            public string press_Ctrl_C_to_interrupt()
                => $"   - press Ctrl-C to interrupt";
            public string Usage_program(string platformDependentCommandLineInvitation, string programName)
                => $"Usage: {platformDependentCommandLineInvitation} {programName} [-h|--help] | [-v|--version] | [--print-app-settings] | [args] ";
            public string Usage_program_with_commands(string platformDependentCommandLineInvitation, string programName)
                => $"Usage: {platformDependentCommandLineInvitation} {programName} [-h|--help] | [-v|--version] | [--print-app-settings] | <command-name> [args] ";
            public string Usage_command(string platformDependentCommandLineInvitation, string programName, string commandName)
                => $"Usage: {platformDependentCommandLineInvitation} {programName} {commandName} [-h|--help] | ";

            public string Available_commands()
                => $"Available commands: ";
            public string the_data_type_is_enum_with_possible_values(string typeName, string possibleValues)
                => $"   - the data type is enum [{typeName}], possible values: {possibleValues}";
            public string the_array_of_enums_with_possible_values(string typeName, string possibleValues)
                => $"   - the array of enums [{typeName.ToLower()}], possible values: {possibleValues}";
            public string the_data_type_is_(string typeName)
                => $"   - the data type is [{typeName.ToLower()}] ";
            public string allowed_value_range_is_between_minVal_and_maxVal(string minVal, string maxVal)
                => $"   - allowed value range is between {minVal} and {maxVal}";
            public string allowed_regex_pattern_is_pattern(string pattern)
                => $"   - allowed regex pattern is \"{pattern}\"";
            public string is_required_to_provide()
                => $"   - is required to provide";
            public string default_value_is(string str)
                => $"   - optional, default value is {str}";
            public string sample_value_is(string str)
                => $"   - sample value is {str}";
            public string includes_rest_of_commandline_arguments()
                => $"   - includes rest of commandline arguments";
            public string Please_retry()
                => "Please retry...";
            public string User_decided_to_discontinue_the_process()
                => "User decided to discontinue the process";
            public string Running_with_arguments()
                => $"Running with arguments:";
            public string Sample_colon()
                => "Sample: ";
            public string All_allowed_keys_within_appSettings_of_app_config_file(string programName)
                => $"All allowed keys within \"appSettings\" of {programName}.config file:";
            public string Error_during_parsing_parameter_ArgumentName_in_appSettings_section_of_app_config(string argumentName, string message)
                => $"Error during parsing parameter {argumentName} in \"appSettings\" section of app.config: {message}";
            public string Could_not_find_referenced_key_xrefName_in_appSettings_section_of_app_config(string xrefName)
                => $"Could not find referenced key \"{xrefName}\" in appSettings section of app.config";
            public string Could_not_parse_value_of_environment_variable(string envVar, string message)
                => $"Could not parse value of environment variable {envVar}: {message}";
            public string All_allowed_environment_variables()
                => $"All allowed environment variables:";
            public string Could_be_passed_via_environment_variable_envVar(string envVar)
                => $"   - could be passed via environment variable ${envVar}";
            public string The_value_is_a_secret()
                => "   - the value is a secret";
            public string CommandName_command_settings(string commandName)
                => $"{commandName} command settings";
            public string Program_settings()
                => "program settings";
        }

        private class RuLiterals : ILocalizedLiterals
        {
            public string The_argument_validation_error_validation_pattern_could_not_be_compiled(string argumentName, string pattern)
                => $"Ошибка валидации аргумента \"{argumentName}\", регулярное выражение \"{pattern}\" является некорректным, и не может быть откомпилировано";
            public string Could_not_find_named_parameter_arg_to_assign_the_argument_value_at_the_argument_position_index(string arg, int index)
                => $"Невозможно найти позиционный параметр \"{arg}\" для присвоения значения по позиции {index + 1}";
            public string The_named_parameter_arg_must_have_an_argument(string arg)
                => $"Именованный параметр '{arg}' должен иметь аргумент в командной строке";
            public string Could_not_find_parameter_to_assign_the_argument_value_arg_at_the_argument_position_positionalArgumentIndex(string arg, int index)
                => $"Невозможно найти позиционный параметр номер: {index + 1} для присвоения значения \"{arg}\"";
            public string The_argument_name_must_be_provided(string argumentName)
                => $"Параметр с именем \"{argumentName}\" является обязательным, но не был предоставлен";
            public string The_argument_with_index_must_be_provided(string argumentName, int index)
                => $"Параметр \"{argumentName}\" с позицией \"{index + 1}\" является обязательным, но не был предоставлен";
            public string Could_not_cast_string_to_the_target_type(string strArgValue, string typeName)
                => $"Невозможно преобразовать строку \"{strArgValue}\" в параметр с типом данных \"{typeName}\"";
            public string Could_not_cast_element_number_i_with_value_equals_element_to_the_target_type(int i, string element, string typeName)
                => $"Невозможно преобразовать элемент массива с номером \"{i + 1}\" и значением = \"{element}\" в целевой тип данных \"{typeName}\"";
            public string Could_not_find_positional_parameter_by_index_It_might_be_too_much_arguments(int index)
                => $"Невозможно найти позиционный параметр по индексу {index + 1}, может быть указали слишком много аргументов?";
            public string Could_not_find_named_parameter_by_short_name_It_might_be_too_much_arguments(string shortName)
                => $"Невозможно найти именованный параметр по короткому имени \"-{shortName}\", может ошиблись в имени?";
            public string Could_not_find_named_parameter_by_long_name_It_might_be_too_much_arguments(string longName)
                => $"Невозможно найти именованный параметр по длинному имени \"--{longName}\", может ошиблись в имени?";
            public string Could_not_find_field_by_name_It_might_be_something_wrong(string name)
                => $"Невозможно найти поле класса по имени \"{name}\", что-то пошло не так...";
            public string Could_not_cast_argument_value_to_the_Int32_type_for_range_validation(string value)
                => $"Невозможно преобразовать значение \"{value}\" в целочисленный тип данных, для валидации по допустимому интервалу значений";
            public string The_value_of_argument_must_be_in_range_between_minVal_and_maxVal(int intVal, string argumentName, string minVal, string maxVal)
                => $"Значение {intVal} параметра \"{argumentName}\" должно быть в интервале от {minVal} до {maxVal} ";
            public string The_argument_validation_error_could_not_parse_array_element_number_i_with_value_equals_element_to_the_type(string argumentName, int i, string element, string typeName)
                => $"Ошибка валидации параметра \"{argumentName}\", невозможно преобразовать элемент массива {i + 1} со значением = \"{element}\" в тип данных {typeName} ";
            public string The_argument_validation_error_value_of_array_element_number_i_must_be_in_range_between_minVal_and_maxVal(string argumentName, int intVal, int i, string minVal, string maxVal)
                => $"Ошибка валидации параметра \"{argumentName}\", значение {intVal} элемента массива {i + 1} должно быть в интервале от {minVal} до {maxVal} ";
            public string The_value_of_argument_could_not_be_verfied_with_pattern_due_to_inner_error(string strNewValue, string argumentName, string pattern)
                => $"Значение \"{strNewValue}\" параметра \"{argumentName}\" не может быть валидировано с помощью выражения \"{pattern}\" из за вложенной ошибки ";
            public string The_value_of_argument_does_not_match_the_pattern(string strNewValue, string argumentName, string pattern)
                => $"Значение \"{strNewValue}\" параметра \"{argumentName}\" не соответствует регулярному выражению \"{pattern}\" ";
            public string The_argument_validation_error_Value_of_array_element_number_i_does_not_match_the_regex_pattern_pattern(string argumentName, string element, int i, string pattern)
                => $"Ошибка валидации параметра \"{argumentName}\", значение \"{element}\" элемента массива номер {i + 1} не соответствует регулярному выражению \"{pattern}\" ";
            public string Could_not_find_the_command(string commandName)
                => $"Не удалось найти запрошенную команду по имени: {commandName}";
            public string Command_line_provided()
                => "Выполняется с командной строкой:";
            public string The_field_does_not_support_interactive_input(string fieldName)
                 => $"Поле \"{fieldName}\" не поддерживает интерактивный ввод!";
            public string The_argument_must_not_be_null_or_empty_string(string argumentName)
                => $"Параметр \"{argumentName}\" должен быть не нулевой и не пустая строка";
            public string just_press_enter_to_use_default_value()
                => $"   - просто нажмите Enter чтобы использовать значение по умолчанию";
            public string press_Ctrl_C_to_interrupt()
                => $"   - нажмите Ctrl-C что бы прервать";
            public string Usage_program(string platformDependentCommandLineInvitation, string programName)
                => $"Использование: {platformDependentCommandLineInvitation} {programName} [-h|--help] | [-v|--version] | [--print-app-settings] | [args] ";
            public string Usage_program_with_commands(string platformDependentCommandLineInvitation, string programName)
                => $"Использование: {platformDependentCommandLineInvitation} {programName} [-h|--help] | [-v|--version] | [--print-app-settings] | <command-name> [args] ";
            public string Usage_command(string platformDependentCommandLineInvitation, string programName, string commandName)
                => $"Использование: {platformDependentCommandLineInvitation} {programName} {commandName} [-h|--help] | ";
            public string Available_commands()
                => $"Доступные команды: ";
            public string the_data_type_is_enum_with_possible_values(string typeName, string possibleValues)
                => $"   - тип данных перечисление [{typeName}], возможные значение: {possibleValues}";
            public string the_array_of_enums_with_possible_values(string typeName, string possibleValues)
                => $"   - массив типа перечисление [{typeName.ToLower()}], возможные значения: {possibleValues}";
            public string the_data_type_is_(string typeName)
                => $"   - тип данных [{typeName.ToLower()}] ";
            public string allowed_value_range_is_between_minVal_and_maxVal(string minVal, string maxVal)
                => $"   - ограничено интервалом от {minVal} до {maxVal}";
            public string allowed_regex_pattern_is_pattern(string pattern)
                => $"   - значение ограничено регулярным выражением \"{pattern}\"";
            public string is_required_to_provide()
                => $"   - обязательный";
            public string default_value_is(string str)
                => $"   - необязательный, значение по умолчанию {str}";
            public string sample_value_is(string str)
                => $"   - например {str}";
            public string includes_rest_of_commandline_arguments()
                => $"   - включает все аргументы до конца строки";
            public string Please_retry()
                 => "Попробуйте снова...";
            public string User_decided_to_discontinue_the_process()
                => "Пользователь решил прервать процесс";
            public string Running_with_arguments()
                => $"Выполняется с параметрами:";
            public string Sample_colon()
                => "Пример: ";
            public string All_allowed_keys_within_appSettings_of_app_config_file(string programName)
                => $"Допустимые параметры в секции \"appSettings\" файла {programName}.config:";
            public string Error_during_parsing_parameter_ArgumentName_in_appSettings_section_of_app_config(string argumentName, string message)
                => $"Ошибка в процессе попытки разбора значения параметра \"{argumentName}\" в секции \"appSettings\" файла app.config: {message}";
            public string Could_not_find_referenced_key_xrefName_in_appSettings_section_of_app_config(string xrefName)
                 => $"Невозможно найти параметр по ссылке \"{xrefName}\" в секции appSettings файла app.config";
            public string Could_not_parse_value_of_environment_variable(string envVar, string message)
                => $"Возникли ошибки при анализе значения переменной среды окружения \"{envVar}\": {message}";
            public string All_allowed_environment_variables()
                => $"Допустимые переменные среды окружения:";
            public string Could_be_passed_via_environment_variable_envVar(string envVar)
                => $"   - может быть передан через переменную среды окружения: ${envVar}";
            public string The_value_is_a_secret()
                => "   - значение является секретом";
            public string CommandName_command_settings(string commandName)
                => $"Параметры команды {commandName}";
            public string Program_settings()
                => "Параметры программы";
        }

        internal static class ConsoleReadLine 
        {
            public static string ReadSecret()
            {
                const char SpaceChar = ' ';
                const char StarChar = '*';
                const int WaitBetweenKeysAvailabilityChecksInMilliseconds = 50;
                const int ShowKeysMaxTimeInMilliseconds = 3000;
                bool shownInput = false;
                string inputString = String.Empty;
                DateTime shownInputTime = DateTime.MinValue;
                do
                {
                    if (Console.KeyAvailable)
                    {
                        var keyInfo = Console.ReadKey(true);

                        // any key pressed but input has been shown
                        if (shownInput)
                        {
                            var newPos = NewCursorPositionForShowHideInput(
                                Console.CursorTop, Console.CursorLeft, Console.BufferWidth, inputString.Length);
                            Console.CursorLeft = newPos.left;
                            Console.CursorTop = newPos.top;
                            var stars = new String(StarChar, inputString.Length);
                            Console.Write(stars);
                            shownInput = false;
                        }

                        // Handle F2 or Tab key
                        if ((keyInfo.Key == ConsoleKey.Tab) || (keyInfo.Key == ConsoleKey.F2))
                        {
                            var newPos = NewCursorPositionForShowHideInput(
                                Console.CursorTop, Console.CursorLeft, Console.BufferWidth, inputString.Length);
                            Console.CursorLeft = newPos.left;
                            Console.CursorTop = newPos.top;
                            Console.Write(inputString);
                            shownInput = true;
                            shownInputTime = DateTime.Now;
                            continue;
                        }

                        // Skip ALT modifiers
                        if ((keyInfo.Modifiers & ConsoleModifiers.Alt) == ConsoleModifiers.Alt)
                            continue;

                        // Skip CTRL modifiers
                        if ((keyInfo.Modifiers & ConsoleModifiers.Control) == ConsoleModifiers.Control)
                            continue;


                        // Handle enter
                        if (keyInfo.Key == ConsoleKey.Enter)
                        {
                            break;
                        }

                        // Handle Escape key.
                        if (keyInfo.Key == ConsoleKey.Escape)
                        {
                            inputString = string.Empty;
                            break;
                        }

                        // Handle backspace.
                        if (keyInfo.Key == ConsoleKey.Backspace)
                        {
                            if (inputString.Length >= 1)
                            {
                                // remove last character
                                inputString = inputString.Substring(0, inputString.Length - 1);
                                // put cursor to the left col or top row and end of line if it is already on leftmost column
                                Console.CursorVisible = false;
                                int cursorLeft = Console.CursorLeft;
                                int cursorTop = Console.CursorTop;
                                int bufferWidth = Console.BufferWidth;
                                if (cursorLeft > 0)
                                {
                                    Console.CursorLeft = Math.Max(cursorLeft - 1, 0);
                                    Console.Write(SpaceChar);
                                    Console.CursorLeft = Math.Max(cursorLeft - 1, 0);
                                }
                                else
                                {
                                    Console.CursorLeft = Math.Max(bufferWidth - 1, 0);
                                    Console.CursorTop = Math.Max(cursorTop - 1, 0);
                                    // write space to remove right side character
                                    Console.Write(SpaceChar);
                                    Console.CursorLeft = Math.Max(bufferWidth - 1, 0);
                                    Console.CursorTop = Math.Max(cursorTop - 1, 0);
                                }
                                Console.CursorVisible = true;
                            }
                            continue;
                        }

                        // Ignore if KeyChar value is \u0000.
                        if (keyInfo.KeyChar == '\u0000')
                            continue;

                        // Handle key by adding it to input string.
                        char ch = keyInfo.KeyChar;

                        // echo star instead of original character
                        Console.Write(StarChar);

                        inputString += ch;
                    }
                    else
                    {
                        // check if the secret is shown but timeout has passed
                        if (shownInput)
                        {
                            var now = DateTime.Now;
                            if (shownInputTime.AddMilliseconds(ShowKeysMaxTimeInMilliseconds) < now)
                            {
                                var newPos = NewCursorPositionForShowHideInput(
                                    Console.CursorTop, Console.CursorLeft, Console.BufferWidth, inputString.Length);
                                Console.CursorLeft = newPos.left;
                                Console.CursorTop = newPos.top;
                                var stars = new String(StarChar, inputString.Length);
                                Console.Write(stars);
                                shownInput = false;
                            }
                        }
                        Thread.Sleep(WaitBetweenKeysAvailabilityChecksInMilliseconds);
                    }
                } while (true);

                Console.WriteLine();
                return inputString;
            }

            private static (int top, int left) NewCursorPositionForShowHideInput(int cursorTop, int cursorLeft, int bufferWidth, int inputLength)
            {
                int newCursorLeft = -1;
                int newCursorTop = -1;
                if (inputLength <= cursorLeft)
                {
                    newCursorLeft = Math.Max(cursorLeft - inputLength, 0);
                    newCursorTop = cursorTop;
                }
                else
                {
                    int fullRows = (inputLength - cursorLeft) / bufferWidth;
                    int firstLineLen = (inputLength - cursorLeft) % bufferWidth;
                    newCursorLeft = Math.Max(bufferWidth - firstLineLen, 0);
                    newCursorTop = Math.Max(cursorTop - (fullRows + 1), 0);
                }
                return (newCursorTop, newCursorLeft);
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class PositionalAttribute : Attribute
        {
            public PositionalAttribute()
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class AllowedRangeAttribute : Attribute
        {
            public AllowedRangeAttribute(int min, int max = Int32.MaxValue)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class AllowedRegexPatternAttribute : Attribute
        {
            public AllowedRegexPatternAttribute(string regex)
            {
            }
            public AllowedRegexPatternAttribute(string regex, bool igoreCase)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Field)]
        public class DocAttribute : Attribute
        {
            public DocAttribute(string documentation)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Class)]
        public class GenerateSampleAttribute : Attribute
        {
            public GenerateSampleAttribute()
            {
            }
            public GenerateSampleAttribute(string title)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class SampleValueAttribute : Attribute
        {
            public SampleValueAttribute()
            {
            }
            public SampleValueAttribute(Object value)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class NamedAttribute : Attribute
        {
            public NamedAttribute()
            {
            }
            public NamedAttribute(Char shortArgumentName)
            {
            }
            public NamedAttribute(string longArgumentName)
            {
            }
        }
        [AttributeUsage(AttributeTargets.Field)]
        public class RequiredAttribute : Attribute
        {
            public RequiredAttribute()
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class RestOfArgumentsAttribute : Attribute
        {
            public RestOfArgumentsAttribute()
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class InteractiveAttribute : Attribute
        {
            public InteractiveAttribute(string label, bool useDafultIfEmpty)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class AppSettingsAttribute : Attribute
        {
            public AppSettingsAttribute()
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class SecretAttribute : Attribute
        {
            public SecretAttribute()
            {
            }
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class EnvironmentVariableAttribute : Attribute
        {
            public EnvironmentVariableAttribute()
            {
            }
            public EnvironmentVariableAttribute(string environmentVariable)
            {
            }
        }

        [AttributeUsage(AttributeTargets.Class)]
        public class DefaultCommandAttribute : Attribute
        {
            public DefaultCommandAttribute()
            {
            }
        }

        public class ArgumentParseException : ArgumentException
        {
            public Object Command { get; private set; }
            public ArgumentParseException(string message, Object obj) : base(message) { Command = obj;  }
            public ArgumentParseException(string message, Object obj, Exception innerException) : base(message, innerException) { Command = obj; }
        }

        public class UnknownCommandException : Exception
        {
            public UnknownCommandException(string message) : base(message) { }
        }

        public class PrintVersionException : Exception
        {
            public PrintVersionException() : base() { }
        }

        public class PrintAppSettingsException : Exception
        {
            public PrintAppSettingsException() : base() { }
        }

        public class ProgramHelpException : Exception
        {
            public readonly HelpType HelpType;
            public ProgramHelpException(HelpType helpType) : base() { this.HelpType = helpType;  }
        }

        public class CommandHelpException : Exception
        {
            public readonly HelpType HelpType;
            public readonly ICommand Command;
            public CommandHelpException(ICommand cmd, HelpType helpType) : base() { this.Command = cmd; HelpType = helpType; }
        }
        public class UserInterruptedInputException : Exception
        {
            public UserInterruptedInputException() : base("Input has been interrupted by user") { }
            public UserInterruptedInputException(string message) : base(message) { }
            public UserInterruptedInputException(string message, Exception innerException) : base(message, innerException) { }
        }

        public interface ICommand
        {
            string CommandName { get; }
            void Exec();
        };
    }
}
