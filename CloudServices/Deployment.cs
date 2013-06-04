﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;

namespace Linq2Azure.CloudServies
{
    public class Deployment
    {
        public string Name { get; private set; }
        public string Url { get; private set; }
        public DeploymentSlot Slot { get; private set; }
        public string PrivateID { get; private set; }
        public string Label { get; set; }
        public ServiceConfiguration Configuration { get; set; }
        public LatentSequence<RoleInstance> RoleInstances { get; private set; }
        public CloudService Parent { get; private set; }

        Deployment()
        {
            RoleInstances = new LatentSequence<RoleInstance>(GetRoleInstancesAsync);
        }

        public Deployment(string deploymentName, DeploymentSlot deploymentSlot, ServiceConfiguration serviceConfig) : this()
        {
            Contract.Requires(deploymentName != null);
            Contract.Requires(serviceConfig != null);

            Name = Label = deploymentName;
            Slot = deploymentSlot;
            Configuration = serviceConfig;
        }

        internal Deployment(XElement element, CloudService parent) : this()
        {
            Contract.Requires(element != null);
            Contract.Requires(parent != null);

            Parent = parent;
            PopulateFromXml(element);
        }

        void PopulateFromXml(XElement element)
        {
            element.HydrateObject(XmlNamespaces.WindowsAzure, this);
            Slot = (DeploymentSlot)Enum.Parse(typeof(DeploymentSlot), (string)element.Element(XmlNamespaces.WindowsAzure + "DeploymentSlot"), true);
            if (!string.IsNullOrEmpty (Label)) Label = Label.FromBase64String();
            Configuration = new ServiceConfiguration(XElement.Parse(element.Element(XmlNamespaces.WindowsAzure + "Configuration").Value.FromBase64String()));
        }

        public async Task CreateAsync(CloudService parent, Uri packageUrl, CreationOptions options = null)
        {
            Contract.Requires(parent != null);
            Contract.Requires(packageUrl != null);
            Contract.Requires(!string.IsNullOrWhiteSpace(Label));
            Contract.Requires(Configuration != null);

            if (options == null) options = new CreationOptions();
            var ns = XmlNamespaces.WindowsAzure;
            var content = new XElement(ns + "CreateDeployment",
                new XElement(ns + "Name", Name),
                new XElement(ns + "PackageUrl", packageUrl.ToString()),
                new XElement(ns + "Label", Label.ToBase64String()),
                new XElement(ns + "Configuration", Configuration.ToXml().ToString().ToBase64String()),
                new XElement(ns + "StartDeployment", options.StartDeployment),
                new XElement(ns + "TreatWarningsAsError", options.TreatWarningsAsError)
                );

            HttpResponseMessage response = await GetRestClient(parent).PostAsync(content);
            await parent.Subscription.WaitForOperationCompletionAsync(response);
            Parent = parent;
        }

        public async Task RefreshAsync()
        {
            Contract.Requires(Parent != null);
            XElement xe = await GetRestClient().GetXmlAsync();
            PopulateFromXml(xe);
        }

        public async Task UpdateConfigurationAsync()
        {
            Contract.Requires(Parent != null);
            
            var ns = XmlNamespaces.WindowsAzure;
            var content = new XElement(ns + "ChangeConfiguration",
                new XElement(ns + "Configuration", Configuration.ToXml().ToString().ToBase64String()));

            // With the deployments endpoint, you need a forward slash separating the URI from the query string!
            HttpResponseMessage response = await GetRestClient(Parent, "/?comp=config").PostAsync(content);
            await Parent.Subscription.WaitForOperationCompletionAsync(response);
        }

        public Task StartAsync()
        {
            Contract.Requires(Parent != null);
            return UpdateDeploymentStatusAsync("Running");
        }

        public Task StopAsync()
        {
            Contract.Requires(Parent != null);
            return UpdateDeploymentStatusAsync("Suspended");
        }

        async Task UpdateDeploymentStatusAsync(string status)
        {
            var ns = XmlNamespaces.WindowsAzure;
            var content = new XElement(ns + "UpdateDeploymentStatus", new XElement(ns + "Status", status));

            // With the deployments endpoint, you need a forward slash separating the URI from the query string!
            HttpResponseMessage response = await GetRestClient("/?comp=status").PostAsync(content);
            await Parent.Subscription.WaitForOperationCompletionAsync(response);
        }

        public async Task DeleteAsync()
        {
            Contract.Requires(Parent != null);
            await Parent.Subscription.WaitForOperationCompletionAsync(await GetRestClient().DeleteAsync());
            Parent = null;
        }

        async Task<RoleInstance[]> GetRoleInstancesAsync()
        {
            Contract.Requires(Parent != null);
            XElement xe = await GetRestClient().GetXmlAsync();
            return xe.Element(XmlNamespaces.WindowsAzure + "RoleInstanceList")
                .Elements(XmlNamespaces.WindowsAzure + "RoleInstance")
                .Select(r => new RoleInstance(r))
                .ToArray();
        }

        AzureRestClient GetRestClient(string pathSuffix = null) { return GetRestClient(Parent, pathSuffix); }

        AzureRestClient GetRestClient(CloudService cloudService, string pathSuffix = null)
        {
            string servicePath = "services/hostedservices/" + cloudService.Name + "/deploymentslots/" + Slot.ToString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(pathSuffix)) servicePath += pathSuffix;
            return cloudService.Subscription.GetCoreRestClient(servicePath);
        }

        public class CreationOptions
        {
            public bool StartDeployment { get; set; }
            public bool TreatWarningsAsError { get; set; }
        }
    }

    public enum DeploymentSlot { Production, Staging }
}