﻿namespace tomenglertde.Wax.Model.Tools
{
    using System.Diagnostics.Contracts;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Xml;
    using System.Xml.Serialization;

    public static class SerializerExtensions
    {
        public static T Deserialize<T>(this string data) where T : class, new()
        {
            Contract.Ensures(Contract.Result<T>() != null);

            if (string.IsNullOrEmpty(data))
                return new T();

            var serializer = new XmlSerializer(typeof(T));

            try
            {
                var xmlReader = new XmlTextReader(new StringReader(data));

                if (serializer.CanDeserialize(xmlReader))
                    return (serializer.Deserialize(xmlReader) as T) ?? new T();
            }
            catch
            {
            }

            return new T();
        }

        public static string Serialize<T>(this T value)
        {
            Contract.Ensures(Contract.Result<string>() != null);

            var result = new StringBuilder();

            var serializer = new XmlSerializer(typeof(T));

            using (var stringWriter = new StringWriter(result, CultureInfo.InvariantCulture))
            {
                serializer.Serialize(stringWriter, value);
            }

            return result.ToString();
        }
    }
}
