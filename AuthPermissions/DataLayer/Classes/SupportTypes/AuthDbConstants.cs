﻿// Copyright (c) 2021 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

namespace AuthPermissions.DataLayer.Classes.SupportTypes
{
    /// <summary>
    /// Various constants to do with the AuthPermissionsDbContext 
    /// </summary>
    public static class AuthDbConstants
    {
        /// <summary>
        /// Max size of the UserId string
        /// </summary>
        public const int UserIdSize = 256; //must be below 900 bytes because it has a unique index
        /// <summary>
        /// Max size of the UserName string
        /// </summary>
        public const int EmailSize = 256;//must be below 900 bytes because it has a unique index
        /// <summary>
        /// Max size of the UserName string
        /// </summary>
        public const int UserNameSize = 128;
        /// <summary>
        /// Max size of the RoleName string
        /// </summary>
        public const int RoleNameSize = 100;
        /// <summary>
        /// Max size of the TenantName string
        /// </summary>
        public const int TenantNameSize = 100;
        /// <summary>
        /// Max size of the TenantDataKey string
        /// </summary>
        public const int TenantDataKeySize = 100;

        /// <summary>
        /// the name of the EF Core migration 
        /// </summary>
        public const string MigrationsHistoryTableName = "__AuthPermissionsMigrationHistory";
    }
}