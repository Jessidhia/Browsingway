﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace Browsingway.Common.TinyJson;

// TODO: replace with .net build in Json classes
#nullable disable

// Really simple JSON parser in ~300 lines
// - Attempts to parse JSON files with minimal GC allocation
// - Nice and simple "[1,2,3]".FromJson<List<int>>() API
// - Classes and structs can be parsed too!
//      class Foo { public int Value; }
//      "{\"Value\":10}".FromJson<Foo>()
// - Can parse JSON without type information into Dictionary<string,object> and List<object> e.g.
//      "[1,2,3]".FromJson<object>().GetType() == typeof(List<object>)
//      "{\"Value\":10}".FromJson<object>().GetType() == typeof(Dictionary<string,object>)
// - No JIT Emit support to support AOT compilation on iOS
// - Attempts are made to NOT throw an exception if the JSON is corrupted or invalid: returns null instead.
// - Only public fields and property setters on classes/structs will be written to
//
// Limitations:
// - No JIT Emit support to parse structures quickly
// - Limited to parsing <2GB JSON files (due to int.MaxValue)
// - Parsing of abstract classes or interfaces is NOT supported and will throw an exception.
internal static class JsonParser
{
	[ThreadStatic]
	private static Stack<List<string>> _splitArrayPool;

	[ThreadStatic]
	private static StringBuilder _stringBuilder;

	[ThreadStatic]
	private static Dictionary<Type, Dictionary<string, FieldInfo>> _fieldInfoCache;

	[ThreadStatic]
	private static Dictionary<Type, Dictionary<string, PropertyInfo>> _propertyInfoCache;

	public static T FromJson<T>(string json)
	{
		// Initialize, if needed, the ThreadStatic variables
		if (_propertyInfoCache == null)
		{
			_propertyInfoCache = new Dictionary<Type, Dictionary<string, PropertyInfo>>();
		}

		if (_fieldInfoCache == null)
		{
			_fieldInfoCache = new Dictionary<Type, Dictionary<string, FieldInfo>>();
		}

		if (_stringBuilder == null)
		{
			_stringBuilder = new StringBuilder();
		}

		if (_splitArrayPool == null)
		{
			_splitArrayPool = new Stack<List<string>>();
		}

		//Remove all whitespace not within strings to make parsing simpler
		_stringBuilder.Length = 0;
		for (int i = 0; i < json.Length; i++)
		{
			char c = json[i];
			if (c == '"')
			{
				i = AppendUntilStringEnd(true, i, json);
				continue;
			}

			if (char.IsWhiteSpace(c))
			{
				continue;
			}

			_stringBuilder.Append(c);
		}

		//Parse the thing!
		return (T)ParseValue(typeof(T), _stringBuilder.ToString());
	}

	private static int AppendUntilStringEnd(bool appendEscapeCharacter, int startIdx, string json)
	{
		_stringBuilder.Append(json[startIdx]);
		for (int i = startIdx + 1; i < json.Length; i++)
		{
			if (json[i] == '\\')
			{
				if (appendEscapeCharacter)
				{
					_stringBuilder.Append(json[i]);
				}

				_stringBuilder.Append(json[i + 1]);
				i++; //Skip next character as it is escaped
			}
			else if (json[i] == '"')
			{
				_stringBuilder.Append(json[i]);
				return i;
			}
			else
			{
				_stringBuilder.Append(json[i]);
			}
		}

		return json.Length - 1;
	}

	//Splits { <value>:<value>, <value>:<value> } and [ <value>, <value> ] into a list of <value> strings
	private static List<string> Split(string json)
	{
		List<string> splitArray = _splitArrayPool.Count > 0 ? _splitArrayPool.Pop() : new List<string>();
		splitArray.Clear();
		if (json.Length == 2)
		{
			return splitArray;
		}

		int parseDepth = 0;
		_stringBuilder.Length = 0;
		for (int i = 1; i < json.Length - 1; i++)
		{
			switch (json[i])
			{
				case '[':
				case '{':
					parseDepth++;
					break;
				case ']':
				case '}':
					parseDepth--;
					break;
				case '"':
					i = AppendUntilStringEnd(true, i, json);
					continue;
				case ',':
				case ':':
					if (parseDepth == 0)
					{
						splitArray.Add(_stringBuilder.ToString());
						_stringBuilder.Length = 0;
						continue;
					}

					break;
			}

			_stringBuilder.Append(json[i]);
		}

		splitArray.Add(_stringBuilder.ToString());

		return splitArray;
	}

	internal static object ParseValue(Type type, string json)
	{
		if (type == typeof(string))
		{
			if (json.Length <= 2)
			{
				return string.Empty;
			}

			StringBuilder parseStringBuilder = new(json.Length);
			for (int i = 1; i < json.Length - 1; ++i)
			{
				if (json[i] == '\\' && i + 1 < json.Length - 1)
				{
					int j = "\"\\nrtbf/".IndexOf(json[i + 1]);
					if (j >= 0)
					{
						parseStringBuilder.Append("\"\\\n\r\t\b\f/"[j]);
						++i;
						continue;
					}

					if (json[i + 1] == 'u' && i + 5 < json.Length - 1)
					{
						UInt32 c = 0;
						if (UInt32.TryParse(json.Substring(i + 2, 4), NumberStyles.AllowHexSpecifier, null, out c))
						{
							parseStringBuilder.Append((char)c);
							i += 5;
							continue;
						}
					}
				}

				parseStringBuilder.Append(json[i]);
			}

			return parseStringBuilder.ToString();
		}

		if (type.IsPrimitive)
		{
			object result = Convert.ChangeType(json, type, CultureInfo.InvariantCulture);
			return result;
		}

		if (type == typeof(decimal))
		{
			decimal result;
			decimal.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
			return result;
		}

		if (json == "null")
		{
			return null;
		}

		if (type.IsEnum)
		{
			if (json[0] == '"')
			{
				json = json.Substring(1, json.Length - 2);
			}

			try
			{
				return Enum.Parse(type, json, false);
			}
			catch
			{
				return 0;
			}
		}

		if (type.IsArray)
		{
			Type arrayType = type.GetElementType();
			if (json[0] != '[' || json[json.Length - 1] != ']')
			{
				return null;
			}

			List<string> elems = Split(json);
			Array newArray = Array.CreateInstance(arrayType, elems.Count);
			for (int i = 0; i < elems.Count; i++)
			{
				newArray.SetValue(ParseValue(arrayType, elems[i]), i);
			}

			_splitArrayPool.Push(elems);
			return newArray;
		}

		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
		{
			Type listType = type.GetGenericArguments()[0];
			if (json[0] != '[' || json[json.Length - 1] != ']')
			{
				return null;
			}

			List<string> elems = Split(json);
			IList list = (IList)type.GetConstructor(new[] { typeof(int) }).Invoke(new object[] { elems.Count });
			for (int i = 0; i < elems.Count; i++)
			{
				list.Add(ParseValue(listType, elems[i]));
			}

			_splitArrayPool.Push(elems);
			return list;
		}

		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
		{
			Type keyType, valueType;
			{
				Type[] args = type.GetGenericArguments();
				keyType = args[0];
				valueType = args[1];
			}

			//Refuse to parse dictionary keys that aren't of type string
			if (keyType != typeof(string))
			{
				return null;
			}

			//Must be a valid dictionary element
			if (json[0] != '{' || json[json.Length - 1] != '}')
			{
				return null;
			}

			//The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
			List<string> elems = Split(json);
			if (elems.Count % 2 != 0)
			{
				return null;
			}

			IDictionary dictionary = (IDictionary)type.GetConstructor(new[] { typeof(int) }).Invoke(new object[] { elems.Count / 2 });
			for (int i = 0; i < elems.Count; i += 2)
			{
				if (elems[i].Length <= 2)
				{
					continue;
				}

				string keyValue = elems[i].Substring(1, elems[i].Length - 2);
				object val = ParseValue(valueType, elems[i + 1]);
				dictionary.Add(keyValue, val);
			}

			return dictionary;
		}

		if (type == typeof(object))
		{
			return ParseAnonymousValue(json);
		}

		if (json[0] == '{' && json[json.Length - 1] == '}')
		{
			return ParseObject(type, json);
		}

		return null;
	}

	private static object ParseAnonymousValue(string json)
	{
		if (json.Length == 0)
		{
			return null;
		}

		if (json[0] == '{' && json[json.Length - 1] == '}')
		{
			List<string> elems = Split(json);
			if (elems.Count % 2 != 0)
			{
				return null;
			}

			Dictionary<string, object> dict = new(elems.Count / 2);
			for (int i = 0; i < elems.Count; i += 2)
			{
				dict.Add(elems[i].Substring(1, elems[i].Length - 2), ParseAnonymousValue(elems[i + 1]));
			}

			return dict;
		}

		if (json[0] == '[' && json[json.Length - 1] == ']')
		{
			List<string> items = Split(json);
			List<object> finalList = new(items.Count);
			for (int i = 0; i < items.Count; i++)
			{
				finalList.Add(ParseAnonymousValue(items[i]));
			}

			return finalList;
		}

		if (json[0] == '"' && json[json.Length - 1] == '"')
		{
			string str = json.Substring(1, json.Length - 2);
			return str.Replace("\\", string.Empty);
		}

		if (char.IsDigit(json[0]) || json[0] == '-')
		{
			if (json.Contains("."))
			{
				double result;
				double.TryParse(json, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
				return result;
			}
			else
			{
				int result;
				int.TryParse(json, out result);
				return result;
			}
		}

		if (json == "true")
		{
			return true;
		}

		if (json == "false")
		{
			return false;
		}

		// handles json == "null" as well as invalid JSON
		return null;
	}

	private static Dictionary<string, T> CreateMemberNameDictionary<T>(T[] members) where T : MemberInfo
	{
		Dictionary<string, T> nameToMember = new(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < members.Length; i++)
		{
			T member = members[i];
			if (member.IsDefined(typeof(IgnoreDataMemberAttribute), true))
			{
				continue;
			}

			string name = member.Name;
			if (member.IsDefined(typeof(DataMemberAttribute), true))
			{
				DataMemberAttribute dataMemberAttribute = (DataMemberAttribute)Attribute.GetCustomAttribute(member, typeof(DataMemberAttribute), true);
				if (!string.IsNullOrEmpty(dataMemberAttribute.Name))
				{
					name = dataMemberAttribute.Name;
				}
			}

			nameToMember.Add(name, member);
		}

		return nameToMember;
	}

	private static object ParseObject(Type type, string json)
	{
		object instance = FormatterServices.GetUninitializedObject(type);

		//The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
		List<string> elems = Split(json);
		if (elems.Count % 2 != 0)
		{
			return instance;
		}

		Dictionary<string, FieldInfo> nameToField;
		Dictionary<string, PropertyInfo> nameToProperty;
		if (!_fieldInfoCache.TryGetValue(type, out nameToField))
		{
			nameToField = CreateMemberNameDictionary(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
			_fieldInfoCache.Add(type, nameToField);
		}

		if (!_propertyInfoCache.TryGetValue(type, out nameToProperty))
		{
			nameToProperty = CreateMemberNameDictionary(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
			_propertyInfoCache.Add(type, nameToProperty);
		}

		for (int i = 0; i < elems.Count; i += 2)
		{
			if (elems[i].Length <= 2)
			{
				continue;
			}

			string key = elems[i].Substring(1, elems[i].Length - 2);
			string value = elems[i + 1];

			FieldInfo fieldInfo;
			PropertyInfo propertyInfo;
			if (nameToField.TryGetValue(key, out fieldInfo))
			{
				fieldInfo.SetValue(instance, ParseValue(fieldInfo.FieldType, value));
			}
			else if (nameToProperty.TryGetValue(key, out propertyInfo))
			{
				propertyInfo.SetValue(instance, ParseValue(propertyInfo.PropertyType, value), null);
			}
		}

		return instance;
	}
}