using System;
using Lextm.SharpSnmpLib;
using Raven.Client.Util;
using Raven.Server.ServerWide;

namespace Raven.Server.Monitoring.Snmp.Objects.Server
{
    public class ServerLicenseExpirationLeft : ScalarObjectBase<TimeTicks>
    {
        private readonly ServerStore _store;

        public ServerLicenseExpirationLeft(ServerStore store)
            : base(SnmpOids.Server.ServerLicenseExpirationLeft)
        {
            _store = store;
        }

        protected override TimeTicks GetData()
        {
            var status = _store.LicenseManager.GetLicenseStatus();
            if (status.Expiration.HasValue == false)
                return null;

            var timeLeft = status.Expiration.Value - SystemTime.UtcNow;
            return new TimeTicks(timeLeft.TotalMilliseconds > 0 ? timeLeft : TimeSpan.Zero);
        }
    }
}
