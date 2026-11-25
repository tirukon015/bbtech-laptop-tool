namespace BBTech.Core.Security
{
    /// <summary>
    /// Centralized permissions used across BBTech Laptop Tools for granular access control.
    /// Never trust department name alone or displayed name alone for authorization.
    /// </summary>
    public static class AppPermissions
    {
        public const string All = "ALL";

        // Hardware
        public const string HardwareTest = "hardware.test";

        // Software
        public const string SoftwareTest = "software.test";
        public const string SoftwareInstall = "software.install";
        public const string SoftwareActivationCheck = "software.activation.check";
        public const string SoftwareActivationExecute = "software.activation.execute";

        // QC
        public const string QcInspect = "qc.inspect";
        public const string QcApprove = "qc.approve";

        // E-Commerce
        public const string Ecommerce = "ecommerce";

        // Administration
        public const string UsersManage = "users.manage";
        public const string PermissionsManage = "permissions.manage";
        public const string SoftwarePackagesManage = "software_packages.manage";
        public const string AuditView = "audit.view";
        public const string RecordsDelete = "records.delete";
        public const string ConfigurationManage = "configuration.manage";
    }
}
