﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml;
using Simple.OData.Client.Extensions;

namespace Simple.OData.Client
{
    class AdapterFactory
    {
        private const string AdapterV3AssemblyName = "Simple.OData.Client.V3.Adapter";
        private const string AdapterV4AssemblyName = "Simple.OData.Client.V4.Adapter";
        private const string AdapterV3TypeName = "Simple.OData.Client.V3.Adapter.ODataAdapter";
        private const string AdapterV4TypeName = "Simple.OData.Client.V4.Adapter.ODataAdapter";
        private const string ModelAdapterV3TypeName = "Simple.OData.Client.V3.Adapter.ODataModelAdapter";
        private const string ModelAdapterV4TypeName = "Simple.OData.Client.V4.Adapter.ODataModelAdapter";

        public async Task<IODataModelAdapter> CreateModelAdapterAsync(HttpResponseMessage response)
        {
            var protocolVersions = (await GetSupportedProtocolVersionsAsync(response).ConfigureAwait(false)).ToArray();

            foreach (var protocolVersion in protocolVersions)
            {
                var loadModelAdapter = GetModelAdapterLoader(protocolVersion, response);
                if (loadModelAdapter != null)
                    return loadModelAdapter();
            }
            throw new NotSupportedException(string.Format("OData protocols {0} are not supported", string.Join(",", protocolVersions)));
        }

        public IODataModelAdapter CreateModelAdapter(string metadataString)
        {
            var protocolVersion = GetMetadataProtocolVersion(metadataString);
            var loadModelAdapter = GetModelAdapterLoader(protocolVersion, metadataString);
            if (loadModelAdapter == null)
                throw new NotSupportedException(string.Format("OData protocol {0} is not supported", protocolVersion));

            return loadModelAdapter();
        }

        public Func<ISession, IODataAdapter> CreateAdapter(string metadataString)
        {
            var modelAdapter = CreateModelAdapter(metadataString);

            var loadAdapter = GetAdapterLoader(modelAdapter);
            if (loadAdapter == null)
                throw new NotSupportedException(string.Format("OData protocol {0} is not supported", modelAdapter.ProtocolVersion));

            return loadAdapter;
        }

        private async Task<IEnumerable<string>> GetSupportedProtocolVersionsAsync(HttpResponseMessage response)
        {
            IEnumerable<string> headerValues;
            if (response.Headers.TryGetValues(HttpLiteral.DataServiceVersion, out headerValues) ||
                response.Headers.TryGetValues(HttpLiteral.ODataVersion, out headerValues))
            {
                return headerValues.SelectMany(x => x.Split(';')).Where(x => x.Length > 0);                
            }
            else
            {
                try
                {
                    var metadataString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var protocolVersion = GetMetadataProtocolVersion(metadataString);
                    return new[] { protocolVersion };
                }
                catch (Exception)
                {
                    throw new InvalidOperationException("Unable to identify OData protocol version");
                }
            }
        }

        private Func<ISession, IODataAdapter> GetAdapterLoader(IODataModelAdapter modelAdapter)
        {
            if (modelAdapter.ProtocolVersion == ODataProtocolVersion.V1 ||
                modelAdapter.ProtocolVersion == ODataProtocolVersion.V2 ||
                modelAdapter.ProtocolVersion == ODataProtocolVersion.V3)
                return session => LoadAdapter(AdapterV3AssemblyName, AdapterV3TypeName, session, modelAdapter);
            if (modelAdapter.ProtocolVersion == ODataProtocolVersion.V4)
                return session => LoadAdapter(AdapterV4AssemblyName, AdapterV4TypeName, session, modelAdapter);

            return null;
        }

        private Func<IODataModelAdapter> GetModelAdapterLoader(string protocolVersion, object extraInfo)
        {
            if (protocolVersion == ODataProtocolVersion.V1 ||
                protocolVersion == ODataProtocolVersion.V2 ||
                protocolVersion == ODataProtocolVersion.V3)
                return () => LoadModelAdapter(AdapterV3AssemblyName, ModelAdapterV3TypeName, protocolVersion, extraInfo);
            if (protocolVersion == ODataProtocolVersion.V4)
                return () => LoadModelAdapter(AdapterV4AssemblyName, ModelAdapterV4TypeName, protocolVersion, extraInfo);

            return null;
        }

        private IODataModelAdapter LoadModelAdapter(string modelAdapterAssemblyName, string modelAdapterTypeName, params object[] ctorParams)
        {
            try
            {
				Assembly assembly = Assembly.GetExecutingAssembly();
				var adapterType = assembly.GetType(modelAdapterTypeName);
				if(adapterType == null)
					assembly = LoadAdapterAssembly(modelAdapterAssemblyName);
                var ctor = FindAdapterConstructor(assembly, modelAdapterTypeName, ctorParams);
                return ctor.Invoke(ctorParams) as IODataModelAdapter;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(string.Format("Unable to load OData adapter from assembly {0}", modelAdapterAssemblyName), exception);
            }
        }

        private Assembly LoadAdapterAssembly(string modelAdapterAssemblyName)
        {
            var assemblyName = new AssemblyName(modelAdapterAssemblyName);
            return Assembly.Load(assemblyName);
        }

        private ConstructorInfo FindAdapterConstructor(Assembly assembly, string modelAdapterTypeName, params object[] ctorParams)
        {
            var constructors = assembly.GetType(modelAdapterTypeName).GetDeclaredConstructors();
            return constructors.Single(x =>
                x.GetParameters().Count() == ctorParams.Count() &&
                x.GetParameters().Last().ParameterType.GetTypeInfo().IsAssignableFrom(ctorParams.Last().GetType().GetTypeInfo()));
        }

        private IODataAdapter LoadAdapter(string adapterAssemblyName, string adapterTypeName, params object[] ctorParams)
        {
            try
            {
				Assembly assembly = Assembly.GetExecutingAssembly();
				var adapterType = assembly.GetType(adapterTypeName);
				if (adapterType == null)
				{
					var assemblyName = new AssemblyName(adapterAssemblyName);
					assembly = Assembly.Load(assemblyName);
					adapterType = assembly.GetType(adapterTypeName);
				}

                var constructors = adapterType.GetDeclaredConstructors();

                var ctor = constructors.Single(x =>
                    x.GetParameters().Count() == ctorParams.Count() &&
                    x.GetParameters().Last().ParameterType.IsInstanceOfType(ctorParams.Last()));

                return ctor.Invoke(ctorParams) as IODataAdapter;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(string.Format("Unable to load OData adapter from assembly {0}", adapterAssemblyName), exception);
            }
        }

        private string GetMetadataProtocolVersion(string metadataString)
        {
            using (var reader = XmlReader.Create(new StringReader(metadataString)))
            {
                reader.MoveToContent();

                var protocolVersion = reader.GetAttribute("Version");

                if (protocolVersion == ODataProtocolVersion.V1 ||
                    protocolVersion == ODataProtocolVersion.V2 ||
                    protocolVersion == ODataProtocolVersion.V3)
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            var version = reader.GetAttribute("m:" + HttpLiteral.MaxDataServiceVersion);
                            if (string.IsNullOrEmpty(version))
                                version = reader.GetAttribute("m:" + HttpLiteral.DataServiceVersion);
                            if (!string.IsNullOrEmpty(version) && string.Compare(version, protocolVersion, StringComparison.Ordinal) > 0)
                                protocolVersion = version;

                            break;
                        }
                    }
                }

                return protocolVersion;
            }
        }
    }
}