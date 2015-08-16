﻿#region License
//   Copyright 2010 John Sheehan
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License. 
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using RestSharp.Extensions;

#if !SILVERLIGHT && !WINDOWS_PHONE
using System.ComponentModel;
#endif

namespace RestSharp.Deserializers
{
    public class XmlDeserializer : IDeserializer
    {
        public string RootElement { get; set; }

        public string Namespace { get; set; }

        public string DateFormat { get; set; }

        public CultureInfo Culture { get; set; }

        public XmlDeserializer()
        {
            Culture = CultureInfo.InvariantCulture;
        }

        public virtual T Deserialize<T>(IRestResponse response)
        {
            if (string.IsNullOrEmpty(response.Content))
                return default(T);

            XDocument doc = XDocument.Parse(response.Content);
            XElement root = doc.Root;

            if (RootElement.HasValue() && doc.Root != null)
            {
                root = doc.Root.Element(RootElement.AsNamespaced(Namespace));
            }

            // autodetect xml namespace
            if (!Namespace.HasValue())
            {
                RemoveNamespace(doc);
            }

            T x = Activator.CreateInstance<T>();
            Type objType = x.GetType();

            if (objType.IsSubclassOfRawGeneric(typeof(List<>)))
            {
                x = (T) HandleListDerivative(root, objType.Name, objType);
            }
            else
            {
                x = (T) Map(x, root);
            }

            return x;
        }

        private static void RemoveNamespace(XDocument xdoc)
        {
            if (xdoc.Root != null)
            {
                foreach (XElement e in xdoc.Root.DescendantsAndSelf())
                {
                    if (e.Name.Namespace != XNamespace.None)
                    {
                        e.Name = XNamespace.None.GetName(e.Name.LocalName);
                    }

                    if (e.Attributes().Any(a => a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None))
                    {
                        e.ReplaceAttributes(
                            e.Attributes()
                             .Select(a => a.IsNamespaceDeclaration
                                 ? null
                                 : a.Name.Namespace != XNamespace.None
                                     ? new XAttribute(XNamespace.None.GetName(a.Name.LocalName), a.Value)
                                     : a));
                    }
                }
            }
        }

        protected virtual object Map(object x, XElement root)
        {
            Type objType = x.GetType();
            PropertyInfo[] props = objType.GetProperties();

            foreach (PropertyInfo prop in props)
            {
                Type type = prop.PropertyType;
                bool typeIsPublic = type.IsPublic || type.IsNestedPublic;

                if (!typeIsPublic || !prop.CanWrite)
                    continue;

                object[] attributes = prop.GetCustomAttributes(typeof(DeserializeAsAttribute), false);
                XName name;

                if (attributes.Length > 0)
                {
                    DeserializeAsAttribute attribute = (DeserializeAsAttribute) attributes[0];

                    name = attribute.Name.AsNamespaced(Namespace);
                }
                else
                {
                    name = prop.Name.AsNamespaced(Namespace);
                }

                object value = GetValueFromXml(root, name, prop);

                if (value == null)
                {
                    // special case for inline list items
                    if (type.IsGenericType)
                    {
                        Type genericType = type.GetGenericArguments()[0];
                        XElement first = GetElementByName(root, genericType.Name);
                        IList list = (IList) Activator.CreateInstance(type);

                        if (first != null && root != null)
                        {
                            IEnumerable<XElement> elements = root.Elements(first.Name);

                            this.PopulateListFromElements(genericType, elements, list);
                        }

                        prop.SetValue(x, list, null);
                    }
                    continue;
                }

                // check for nullable and extract underlying type
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                {
                    // if the value is empty, set the property to null...
                    if (string.IsNullOrEmpty(value.ToString()))
                    {
                        prop.SetValue(x, null, null);
                        continue;
                    }

                    type = type.GetGenericArguments()[0];
                }

                if (type == typeof(bool))
                {
                    string toConvert = value.ToString().ToLower();

                    prop.SetValue(x, XmlConvert.ToBoolean(toConvert), null);
                }
                else if (type.IsPrimitive)
                {
                    prop.SetValue(x, value.ChangeType(type, Culture), null);
                }
                else if (type.IsEnum)
                {
                    object converted = type.FindEnumValue(value.ToString(), Culture);

                    prop.SetValue(x, converted, null);
                }
                else if (type == typeof(Uri))
                {
                    Uri uri = new Uri(value.ToString(), UriKind.RelativeOrAbsolute);

                    prop.SetValue(x, uri, null);
                }
                else if (type == typeof(string))
                {
                    prop.SetValue(x, value, null);
                }
                else if (type == typeof(DateTime))
                {
                    value = this.DateFormat.HasValue()
                                ? DateTime.ParseExact(value.ToString(), this.DateFormat, this.Culture)
                                : DateTime.Parse(value.ToString(), this.Culture);

                    prop.SetValue(x, value, null);
                }
                else if (type == typeof(DateTimeOffset))
                {
                    string toConvert = value.ToString();

                    if (!string.IsNullOrEmpty(toConvert))
                    {
                        DateTimeOffset deserialisedValue;

                        try
                        {
                            deserialisedValue = XmlConvert.ToDateTimeOffset(toConvert);
                            prop.SetValue(x, deserialisedValue, null);
                        }
                        catch (Exception)
                        {
                            object result;

                            if (TryGetFromString(toConvert, out result, type))
                            {
                                prop.SetValue(x, result, null);
                            }
                            else
                            {
                                //fallback to parse
                                deserialisedValue = DateTimeOffset.Parse(toConvert);
                                prop.SetValue(x, deserialisedValue, null);
                            }
                        }
                    }
                }
                else if (type == typeof(decimal))
                {
                    value = decimal.Parse(value.ToString(), Culture);
                    prop.SetValue(x, value, null);
                }
                else if (type == typeof(Guid))
                {
                    string raw = value.ToString();

                    value = string.IsNullOrEmpty(raw) ? Guid.Empty : new Guid(value.ToString());
                    prop.SetValue(x, value, null);
                }
                else if (type == typeof(TimeSpan))
                {
                    TimeSpan timeSpan = XmlConvert.ToTimeSpan(value.ToString());

                    prop.SetValue(x, timeSpan, null);
                }
                else if (type.IsGenericType)
                {
                    Type t = type.GetGenericArguments()[0];
                    IList list = (IList) Activator.CreateInstance(type);
                    XElement container = GetElementByName(root, prop.Name.AsNamespaced(Namespace));

                    if (container.HasElements)
                    {
                        XElement first = container.Elements().FirstOrDefault();

                        if (first != null)
                        {
                            IEnumerable<XElement> elements = container.Elements(first.Name);

                            this.PopulateListFromElements(t, elements, list);
                        }
                    }

                    prop.SetValue(x, list, null);
                }
                else if (type.IsSubclassOfRawGeneric(typeof(List<>)))
                {
                    // handles classes that derive from List<T>
                    // e.g. a collection that also has attributes
                    object list = HandleListDerivative(root, prop.Name, type);

                    prop.SetValue(x, list, null);
                }
                else
                {
                    //fallback to type converters if possible
                    object result;

                    if (TryGetFromString(value.ToString(), out result, type))
                    {
                        prop.SetValue(x, result, null);
                    }
                    else
                    {
                        // nested property classes
                        if (root != null)
                        {
                            XElement element = GetElementByName(root, name);

                            if (element != null)
                            {
                                object item = CreateAndMap(type, element);

                                prop.SetValue(x, item, null);
                            }
                        }
                    }
                }
            }

            return x;
        }

        private static bool TryGetFromString(string inputString, out object result, Type type)
        {
#if !SILVERLIGHT && !WINDOWS_PHONE
            TypeConverter converter = TypeDescriptor.GetConverter(type);

            if (converter.CanConvertFrom(typeof(string)))
            {
                result = (converter.ConvertFromInvariantString(inputString));

                return true;
            }

            result = null;

            return false;
#else
            result = null;

            return false;
#endif
        }

        private void PopulateListFromElements(Type t, IEnumerable<XElement> elements, IList list)
        {
            foreach (object item in elements.Select(element => this.CreateAndMap(t, element)))
            {
                list.Add(item);
            }
        }

        private object HandleListDerivative(XElement root, string propName, Type type)
        {
            Type t = type.IsGenericType ? type.GetGenericArguments()[0] : type.BaseType.GetGenericArguments()[0];
            IList list = (IList) Activator.CreateInstance(type);
            IList<XElement> elements = root.Descendants(t.Name.AsNamespaced(this.Namespace)).ToList();
            string name = t.Name;
            DeserializeAsAttribute attribute = t.GetAttribute<DeserializeAsAttribute>();

            if (attribute != null)
                name = attribute.Name;

            if (!elements.Any())
            {
                XName lowerName = name.ToLower().AsNamespaced(this.Namespace);

                elements = root.Descendants(lowerName).ToList();
            }

            if (!elements.Any())
            {
                XName camelName = name.ToCamelCase(this.Culture).AsNamespaced(this.Namespace);

                elements = root.Descendants(camelName).ToList();
            }

            if (!elements.Any())
            {
                elements = root.Descendants()
                               .Where(e => e.Name.LocalName.RemoveUnderscoresAndDashes() == name)
                               .ToList();
            }

            if (!elements.Any())
            {
                XName lowerName = name.ToLower().AsNamespaced(this.Namespace);

                elements = root.Descendants()
                               .Where(e => e.Name.LocalName.RemoveUnderscoresAndDashes() == lowerName)
                               .ToList();
            }

            this.PopulateListFromElements(t, elements, list);

            // get properties too, not just list items
            // only if this isn't a generic type
            if (!type.IsGenericType)
            {
                this.Map(list, root.Element(propName.AsNamespaced(this.Namespace)) ?? root);
                // when using RootElement, the heirarchy is different
            }

            return list;
        }

        protected virtual object CreateAndMap(Type t, XElement element)
        {
            object item;

            if (t == typeof(string))
            {
                item = element.Value;
            }
            else if (t.IsPrimitive)
            {
                item = element.Value.ChangeType(t, Culture);
            }
            else
            {
                item = Activator.CreateInstance(t);
                Map(item, element);
            }

            return item;
        }

        protected virtual object GetValueFromXml(XElement root, XName name, PropertyInfo prop)
        {
            object val = null;

            if (root != null)
            {
                XElement element = GetElementByName(root, name);

                if (element == null)
                {
                    XAttribute attribute = GetAttributeByName(root, name);

                    if (attribute != null)
                    {
                        val = attribute.Value;
                    }
                }
                else
                {
                    if (!element.IsEmpty || element.HasElements || element.HasAttributes)
                    {
                        val = element.Value;
                    }
                }
            }

            return val;
        }

        protected virtual XElement GetElementByName(XElement root, XName name)
        {
            XName lowerName = name.LocalName.ToLower().AsNamespaced(name.NamespaceName);
            XName camelName = name.LocalName.ToCamelCase(Culture).AsNamespaced(name.NamespaceName);

            if (root.Element(name) != null)
            {
                return root.Element(name);
            }

            if (root.Element(lowerName) != null)
            {
                return root.Element(lowerName);
            }

            if (root.Element(camelName) != null)
            {
                return root.Element(camelName);
            }

            if (name == "Value".AsNamespaced(name.NamespaceName))
            {
                return root;
            }

            // try looking for element that matches sanitized property name (Order by depth)
            return root.Descendants()
                       .OrderBy(d => d.Ancestors().Count())
                       .FirstOrDefault(d => d.Name.LocalName.RemoveUnderscoresAndDashes() == name.LocalName) ??
                   root.Descendants()
                       .OrderBy(d => d.Ancestors().Count())
                       .FirstOrDefault(d => d.Name.LocalName.RemoveUnderscoresAndDashes() == name.LocalName.ToLower());
        }

        protected virtual XAttribute GetAttributeByName(XElement root, XName name)
        {
            List<XName> names = new List<XName>
                                {
                                    name.LocalName,
                                    name.LocalName.ToLower().AsNamespaced(name.NamespaceName),
                                    name.LocalName.ToCamelCase(Culture).AsNamespaced(name.NamespaceName)
                                };

            return root.DescendantsAndSelf()
                       .OrderBy(d => d.Ancestors().Count())
                       .Attributes()
                       .FirstOrDefault(d => names.Contains(d.Name.LocalName.RemoveUnderscoresAndDashes()));
        }
    }
}
