namespace NServiceBus.Gateway.Sending
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Extensibility;
    using Notifications;
    using Receiving;
    using Routing;
    using Routing.Sites;
    using NServiceBus.Routing;
    using Transport;

    class GatewayMessageSender
    {
        public GatewayMessageSender(string inputAddress, IManageReceiveChannels channelManager, MessageNotifier notifier, string localAddress, ConfigurationBasedSiteRouter configRouter)
        {
            this.configRouter = configRouter;
            messageNotifier = notifier;
            this.localAddress = localAddress;
            this.channelManager = channelManager;
            this.inputAddress = inputAddress;
        }

        public async Task SendToDestination(MessageContext context, IDispatchMessages dispatcher, SingleCallChannelForwarder forwarder)
        {
            var intent = GetMessageIntent(context.Headers);

            var destinationSites = GetDestinationSitesFor(context.Headers, intent);

            //if there is more than 1 destination we break it up into multiple dispatches
            if (destinationSites.Count > 1)
            {
                foreach (var destinationSite in destinationSites)
                {
                    await CloneAndSendLocal(context.Body, context.Headers, destinationSite, context.TransportTransaction, dispatcher).ConfigureAwait(false);
                }

                return;
            }

            var destination = destinationSites.FirstOrDefault();

            if (destination == null)
            {
                throw new InvalidOperationException("No destination found for message");
            }

            await SendToSite(context.Body, context.Headers, destination, forwarder).ConfigureAwait(false);
        }

        static MessageIntentEnum GetMessageIntent(Dictionary<string, string> headers)
        {
            if (headers.TryGetValue(Headers.MessageIntent, out var messageIntentString) && Enum.TryParse(messageIntentString, true, out MessageIntentEnum messageIntent))
            {
                return messageIntent;
            }
            return MessageIntentEnum.Send;
        }


        IList<Site> GetDestinationSitesFor(Dictionary<string, string> headers, MessageIntentEnum intent)
        {
            if (intent == MessageIntentEnum.Reply)
            {
                return OriginatingSiteHeaderRouter.GetDestinationSitesFor(headers).ToList();
            }

            var conventionRoutes = KeyPrefixConventionSiteRouter.GetDestinationSitesFor(headers);
            var configuredRoutes = configRouter.GetDestinationSitesFor(headers);

            return conventionRoutes.Concat(configuredRoutes).ToList();
        }

        Task CloneAndSendLocal(byte[] body, Dictionary<string, string> headers, Site destinationSite, TransportTransaction transportTransaction, IDispatchMessages dispatcher)
        {
            headers[Headers.DestinationSites] = destinationSite.Key;

            var message = new OutgoingMessage(headers[Headers.MessageId], headers, body);
            var operation = new TransportOperation(message, new UnicastAddressTag(inputAddress));

            return dispatcher.Dispatch(new TransportOperations(operation), transportTransaction, new ContextBag());
        }

        async Task SendToSite(byte[] body, Dictionary<string, string> headers, Site targetSite, SingleCallChannelForwarder forwarder)
        {
            headers[Headers.OriginatingSite] = GetDefaultAddressForThisSite();

            await forwarder.Forward(body, headers, targetSite).ConfigureAwait(false);

            messageNotifier.RaiseMessageForwarded(localAddress, targetSite.Channel.Type, body, headers);
        }

        string GetDefaultAddressForThisSite()
        {
            var defaultChannel = channelManager.GetDefaultChannel();
            return $"{defaultChannel.Type},{defaultChannel.GetPublicAddress()}";
        }

        IManageReceiveChannels channelManager;
        string localAddress;
        ConfigurationBasedSiteRouter configRouter;
        MessageNotifier messageNotifier;
        string inputAddress;
    }
}