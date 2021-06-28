﻿// Copyright (c) 2021 Jon P Smith, GitHub: JonPSmith, web: http://www.thereformedprogrammer.net/
// Licensed under MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AuthPermissions.CommonCode;
using AuthPermissions.DataLayer.Classes;
using AuthPermissions.DataLayer.EfCode;
using AuthPermissions.SetupCode;
using Microsoft.EntityFrameworkCore;
using StatusGeneric;

namespace AuthPermissions.AdminCode.Services
{
    /// <summary>
    /// This provides CRUD access to the Auth's Users
    /// </summary>
    public class AuthUsersAdminService : IAuthUsersAdminService
    {
        private readonly AuthPermissionsDbContext _context;
        private readonly ISyncAuthenticationUsers _syncAuthenticationUsers;
        private readonly TenantTypes _tenantType;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="context"></param>
        /// <param name="syncAuthenticationUsers">The authentication sync provider</param>
        /// <param name="options">auth options</param>
        public AuthUsersAdminService(AuthPermissionsDbContext context, ISyncAuthenticationUsers syncAuthenticationUsers, IAuthPermissionsOptions options)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _syncAuthenticationUsers = syncAuthenticationUsers; //Can be null
            _tenantType = options.TenantType;
        }

        /// <summary>
        /// This returns a IQueryable of AuthUser, with optional filtering by dataKey (useful for tenant admin
        /// </summary>
        /// <param name="dataKey">optional dataKey. If provided then it only returns AuthUsers that fall within that dataKey</param>
        /// <returns>query on the database</returns>
        public IQueryable<AuthUser> QueryAuthUsers(string dataKey = null)
        {
            return dataKey == null
                ? _context.AuthUsers
                : _context.AuthUsers.Where(x => (x.UserTenant.ParentDataKey + x.TenantId).StartsWith(dataKey));
        }

        /// <summary>
        /// Finds a AuthUser via its UserId
        /// </summary>
        /// <param name="userId"></param>
        /// <returns>AuthUser with UserRoles and UserTenant</returns>
        public async Task<AuthUser> FindAuthUserByUserIdAsync(string userId)
        {
            return await _context.AuthUsers
                .Include(x => x.UserRoles)
                .Include(x => x.UserTenant)
                .SingleOrDefaultAsync(x => x.UserId == userId);
        }

        /// <summary>
        /// Find a AuthUser via its email
        /// </summary>
        /// <param name="email"></param>
        /// <returns>AuthUser with UserRoles and UserTenant</returns>
        public async Task<AuthUser> FindAuthUserByEmailAsync(string email)
        {
            return await _context.AuthUsers
                .Include(x => x.UserRoles)
                .Include(x => x.UserTenant)
                .SingleOrDefaultAsync(x => x.Email == email);
        }

        /// <summary>
        /// This compares the users in the authentication provider against the user's in the AuthP's database.
        /// It creates a list of all the changes (add, update, remove) than need to be applied to the AuthUsers.
        /// This is shown to the admin user to check, and fill in the Roles/Tenant parts for new users
        /// </summary>
        /// <returns>Status, if valid then it contains a list of <see cref="SyncAuthUserWithChange"/>to display</returns>
        public async Task<List<SyncAuthUserWithChange>> SyncAndShowChangesAsync()
        {
            if (_syncAuthenticationUsers == null)
                throw new AuthPermissionsException(
                    $"You must register a {nameof(ISyncAuthenticationUsers)} service via the RegisterAuthenticationProviderReader extension.");

            var authenticationUsers = await _syncAuthenticationUsers.GetAllActiveUserInfoAsync();
            var authUserDictionary = await _context.AuthUsers
                .Include(x => x.UserRoles)
                .Include(x => x.UserTenant)
                .ToDictionaryAsync(x => x.UserId);

            var result = new List<SyncAuthUserWithChange>();
            foreach (var authenticationUser in authenticationUsers)
            {
                if (authUserDictionary.TryGetValue(authenticationUser.UserId, out var authUser))
                {
                    //check if its a change or not
                    var syncChange = new SyncAuthUserWithChange(authenticationUser, authUser);
                    if (syncChange.ProviderChange == SyncAuthUserChanges.Update)
                        //The two are different so add to the result
                        result.Add(syncChange); 
                    //Removed the authUser as has been handled
                    authUserDictionary.Remove(authenticationUser.UserId);
                }
                else
                {
                    //A new AuthUser should be created
                    result.Add(new SyncAuthUserWithChange(authenticationUser, null));
                }
            }

            //All the authUsers still in the authUserDictionary are not in the authenticationUsers, so mark as remove
            result.AddRange(authUserDictionary.Values.Select(x => new SyncAuthUserWithChange(null, x)));

            return result;
        }

        /// <summary>
        /// This receives a list of <see cref="SyncAuthUserWithChange"/> and applies them to the AuthP database.
        /// This uses the <see cref="SyncAuthUserWithChange.ConfirmChange"/> parameter to define what to change
        /// </summary>
        /// <param name="changesToApply"></param>
        /// <returns>Status</returns>
        public async Task<IStatusGeneric> ApplySyncChangesAsync(List<SyncAuthUserWithChange> changesToApply)
        {
            var status = new StatusGenericHandler();

            foreach (var syncChange in changesToApply)
            {
                switch (syncChange.ConfirmChange)
                {
                    case SyncAuthUserChanges.NoChange:
                        continue;
                    case SyncAuthUserChanges.Add:
                        status.CombineStatuses(await AddUpdateAuthUserAsync(syncChange, false));
                        break;
                    case SyncAuthUserChanges.Update:
                        status.CombineStatuses(await AddUpdateAuthUserAsync(syncChange, true));
                        break;
                    case SyncAuthUserChanges.Remove:
                        var authUserToRemove = await FindAuthUserByUserIdAsync(syncChange.UserId);
                        if (authUserToRemove == null)
                            throw new AuthPermissionsException(
                                $"This should have loaded a AuthUser with the userId of {syncChange.UserId}");
                        _context.Remove(authUserToRemove);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            status.CombineStatuses(await _context.SaveChangesWithUniqueCheckAsync());
            //Build useful summary
            var changeStrings = Enum.GetValues<SyncAuthUserChanges>().ToList()
                .Select(x => $"{x} = {changesToApply.Count(y => y.ConfirmChange == x)}");
            status.Message = $"Sync successful: {(string.Join(", ", changeStrings))}";

            return status;
        }

        private async Task<IStatusGeneric> AddUpdateAuthUserAsync(SyncAuthUserWithChange newUserData, bool update)
        {
            var status = new StatusGenericHandler();
            var roles = newUserData.RoleNames == null
                ? new List<RoleToPermissions>()
                : await _context.RoleToPermissions.Where(x => newUserData.RoleNames.Contains(x.RoleName))
                .ToListAsync();

            if (roles.Count < (newUserData.RoleNames?.Count ?? 0))
            {
                //Could not find one or more Roles
                var missingRoleNames = newUserData.RoleNames;
                roles.ForEach(x => missingRoleNames.Remove(x.RoleName));

                return status.AddError(
                    $"The following role names were not found: {string.Join(", ", missingRoleNames)}");
            }

            Tenant tenant = null;         
            if (newUserData.TenantName != null)
            {
                tenant = await _context.Tenants.SingleOrDefaultAsync(x => x.TenantName == newUserData.TenantName);
                if (tenant == null)
                    return status.AddError($"Could not find the tenant {newUserData.TenantName}");
            }

            //If all ok then we can add/update
            if(!update)
                //Simple add
                _context.Add(new AuthUser(newUserData.UserId, newUserData.Email, newUserData.UserName, roles, tenant));
            else
            {
                var existingAuthUser = await FindAuthUserByUserIdAsync(newUserData.UserId);
                if (existingAuthUser == null)
                    throw new AuthPermissionsException(
                        $"This should have loaded a AuthUser with the userId of {newUserData.UserId}");
                existingAuthUser.ChangeEmail(newUserData.Email); //if same then ignored
                existingAuthUser.ChangeUserName(newUserData.UserName);//if same then ignored
                existingAuthUser.UpdateUserTenant(tenant);//if same then ignored
                if (newUserData.RoleNames.OrderBy(x => x) == existingAuthUser.UserRoles.Select(x => x.RoleName).OrderBy(x => x))
                    //Roles have changed
                    existingAuthUser.ReplaceAllRoles(roles);
            }
            return status;
        }

        /// <summary>
        /// This will set the UserName property in the AuthUser
        /// </summary>
        /// <param name="authUser"></param>
        /// <param name="userName">new user name</param>
        /// <returns></returns>
        public async Task<IStatusGeneric> ChangeUserNameAsync(AuthUser authUser, string userName)
        {
            if (authUser == null) throw new ArgumentNullException(nameof(authUser));
            if (string.IsNullOrEmpty(userName))
                throw new AuthPermissionsBadDataException("Cannot be null or an empty string", nameof(userName));

            var status = new StatusGenericHandler { Message = $"Successfully changed the UserName from {authUser.UserName} to {userName}." };
            authUser.ChangeUserName(userName);
            status.CombineStatuses(await _context.SaveChangesWithUniqueCheckAsync());

            return status;
        }

        /// <summary>
        /// This will set the Email property in the AuthUser
        /// </summary>
        /// <param name="authUser"></param>
        /// <param name="email">new user name</param>
        /// <returns></returns>
        public async Task<IStatusGeneric> ChangeEmailAsync(AuthUser authUser, string email)
        {
            if (authUser == null) throw new ArgumentNullException(nameof(authUser));
            if (string.IsNullOrEmpty(email))
                throw new AuthPermissionsBadDataException("Cannot be null or an empty string", nameof(email));

            var status = new StatusGenericHandler { Message = $"Successfully changed the email from {authUser.Email} to {email}."};

            if (!email.IsValidEmail())
                return status.AddError($"The email '{email}' is not a valid email.");

            authUser.ChangeEmail(email);
            status.CombineStatuses(await _context.SaveChangesWithUniqueCheckAsync());

            return status;
        }

        /// <summary>
        /// This adds a auth role to the auth user
        /// </summary>
        /// <param name="authUser"></param>
        /// <param name="roleName"></param>
        /// <returns></returns>
        public async Task<IStatusGeneric> AddRoleToUser(AuthUser authUser, string roleName)
        {
            if (authUser == null) throw new ArgumentNullException(nameof(authUser));
            if (string.IsNullOrEmpty(roleName))
                throw new AuthPermissionsBadDataException("Cannot be null or an empty string", (nameof(roleName)));
            if (authUser.UserRoles == null)
                throw new AuthPermissionsBadDataException($"The AuthUser's {nameof(AuthUser.UserRoles)} must be loaded", (nameof(authUser)));

            var status = new StatusGenericHandler();

            var role = await _context.RoleToPermissions.SingleOrDefaultAsync(x => x.RoleName == roleName);

            if (role == null)
                return status.AddError($"Could not find the role {roleName}");

            var added = authUser.AddRoleToUser(role);
            status.CombineStatuses(await _context.SaveChangesWithUniqueCheckAsync());

            status.Message = added
                ? $"Successfully added the role {roleName} to auth user {authUser.UserName ?? authUser.Email}."
                : $"The auth user {authUser.UserName ?? authUser.Email} already had the role {roleName}";

            return status;
        }

        /// <summary>
        /// This removes a auth role from the auth user
        /// </summary>
        /// <param name="authUser"></param>
        /// <param name="roleName"></param>
        /// <returns>status</returns>
        public async Task<IStatusGeneric> RemoveRoleToUser(AuthUser authUser, string roleName)
        {
            if (authUser == null) throw new ArgumentNullException(nameof(authUser));
            if (string.IsNullOrEmpty(roleName))
                throw new AuthPermissionsBadDataException("Cannot be null or an empty string", (nameof(roleName)));
            if (authUser.UserRoles == null)
                throw new AuthPermissionsBadDataException($"The AuthUser's {nameof(AuthUser.UserRoles)} must be loaded", (nameof(authUser)));

            var status = new StatusGenericHandler();

            var role = await _context.RoleToPermissions.SingleOrDefaultAsync(x => x.RoleName == roleName);

            if (role == null)
                return status.AddError($"Could not find the role {roleName}");

            var removed = authUser.RemoveRoleFromUser(role);
            status.CombineStatuses(await _context.SaveChangesWithUniqueCheckAsync());

            status.Message = removed
                ? $"Successfully removed the role {roleName} to auth user {authUser.UserName ?? authUser.Email}."
                : $"The auth user {authUser.UserName ?? authUser.Email} didn't have the role {roleName}";

            return status;
        }

        /// <summary>
        /// This allows you to add or change a tenant to a AuthP User
        /// NOTE: you must have set the <see cref="AuthPermissionsOptions.TenantType"/> to a valid tenant type for this to work
        /// </summary>
        /// <param name="authUser"></param>
        /// <param name="tenantFullName">The full name of the tenant</param>
        /// <returns></returns>
        public async Task<IStatusGeneric> ChangeTenantToUserAsync(AuthUser authUser, string tenantFullName)
        {
            if (authUser == null) throw new ArgumentNullException(nameof(authUser));
            if (string.IsNullOrEmpty(tenantFullName))
                throw new AuthPermissionsBadDataException("Cannot be null or an empty string", (nameof(tenantFullName)));

            var status = new StatusGenericHandler
            {
                Message = $"Changed the tenant to {tenantFullName} on auth user {authUser.UserName ?? authUser.Email}."
            };

            if (_tenantType == TenantTypes.NotUsingTenants)
                return status.AddError($"You have not configured the {nameof(AuthPermissionsOptions.TenantType)} to use tenants.");

            var tenant = await _context.Tenants.SingleOrDefaultAsync(x => x.TenantName == tenantFullName);
            if (tenant == null)
                return status.AddError($"Could not find the tenant {tenantFullName}");

            authUser.UpdateUserTenant(tenant);
            status.CombineStatuses(await _context.SaveChangesWithUniqueCheckAsync());

            return status;
        }


    }
}