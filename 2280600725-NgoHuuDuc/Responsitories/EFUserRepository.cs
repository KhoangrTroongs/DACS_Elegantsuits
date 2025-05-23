﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NgoHuuDuc_2280600725.Data;
using NgoHuuDuc_2280600725.Models;
using NgoHuuDuc_2280600725.Models.AccountViewModels;

namespace NgoHuuDuc_2280600725.Responsitories
{
    public class EFUserRepository : IUserRepository
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ApplicationDbContext _context;

        public EFUserRepository(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            IHttpContextAccessor httpContextAccessor,
            ApplicationDbContext context)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _httpContextAccessor = httpContextAccessor;
            _context = context;
        }

        public async Task<IdentityResult> RegisterUserAsync(ApplicationUser identityUser, string password)
        {
            return await _userManager.CreateAsync(identityUser, password);
        }

        public async Task AssignRoleAsync(string email, string role)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user != null)
            {
                if (!await _roleManager.RoleExistsAsync(role))
                {
                    await _roleManager.CreateAsync(new IdentityRole(role));
                }
                await _userManager.AddToRoleAsync(user, role);
            }
        }

        public async Task SignInUserAsync(string email, bool isPersistent)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user != null)
            {
                await _signInManager.SignInAsync(user, isPersistent);
            }
        }

        public async Task<SignInResult> PasswordSignInAsync(string email, string password, bool rememberMe)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user != null)
            {
                // Kiểm tra xem tài khoản có bị khóa không
                if (user.LockoutEnd != null && user.LockoutEnd > DateTimeOffset.Now)
                {
                    return SignInResult.LockedOut;
                }

                // Kiểm tra xem tài khoản có bị vô hiệu hóa không
                if (!user.IsActive)
                {
                    return SignInResult.NotAllowed;
                }

                // Đảm bảo đăng xuất trước khi đăng nhập để tránh xung đột cookie
                await _signInManager.SignOutAsync();

                // Bật tính năng khóa tài khoản sau nhiều lần đăng nhập sai
                var result = await _signInManager.PasswordSignInAsync(user, password, rememberMe, lockoutOnFailure: true);

                // Nếu đăng nhập thành công, ghi log
                if (result.Succeeded)
                {
                    // Tạm thời bỏ cập nhật LastLoginTime vì chưa có migration
                    // user.LastLoginTime = DateTime.Now;
                    // await _userManager.UpdateAsync(user);
                }

                return result;
            }
            return SignInResult.Failed;
        }

        public async Task SignOutAsync()
        {
            await _signInManager.SignOutAsync();
        }

        public async Task<List<ApplicationUser>> GetAllUsersAsync()
        {
            return await _userManager.Users.ToListAsync();
        }

        public async Task<ApplicationUser> GetUserByIdAsync(string id)
        {
            return await _userManager.FindByIdAsync(id);
        }

        public async Task<IdentityResult> UpdateUserAsync(ApplicationUser user)
        {
            return await _userManager.UpdateAsync(user);
        }

        public async Task<IdentityResult> UpdateUserAsync(ApplicationUser user, UserDetailsViewModel model)
        {
            // Cập nhật trực tiếp thuộc tính của ApplicationUser
            user.PhoneNumber = model.PhoneNumber;
            user.UserName = model.Email;
            user.Email = model.Email;
            user.FullName = model.FullName;
            user.Address = model.Address;
            user.DateOfBirth = model.DateOfBirth;

            if (!string.IsNullOrEmpty(model.AvatarUrl))
            {
                user.AvatarUrl = model.AvatarUrl;
            }

            // Cập nhật Claims để đảm bảo tương thích ngược
            var claims = await _userManager.GetClaimsAsync(user);

            await UpdateClaim(user, claims, "FullName", model.FullName);
            await UpdateClaim(user, claims, "Address", model.Address);
            await UpdateClaim(user, claims, "DateOfBirth", model.DateOfBirth.ToString("yyyy-MM-dd"));
            if (!string.IsNullOrEmpty(model.AvatarUrl))
            {
                await UpdateClaim(user, claims, "AvatarUrl", model.AvatarUrl);
            }

            return await _userManager.UpdateAsync(user);
        }

        private async Task UpdateClaim(ApplicationUser user, IList<Claim> existingClaims, string claimType, string claimValue)
        {
            var claim = existingClaims.FirstOrDefault(c => c.Type == claimType);
            if (claim != null)
            {
                await _userManager.RemoveClaimAsync(user, claim);
            }
            await _userManager.AddClaimAsync(user, new Claim(claimType, claimValue));
        }

        public async Task<IdentityResult> DeleteUserAsync(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user != null)
            {
                // Xử lý các liên kết với dữ liệu khác trước khi xóa người dùng
                using (var transaction = await _context.Database.BeginTransactionAsync())
                {
                    try
                    {
                        // Cập nhật đơn hàng: đặt UserId = null thay vì xóa đơn hàng
                        var orders = await _context.Orders.Where(o => o.UserId == id).ToListAsync();
                        foreach (var order in orders)
                        {
                            order.UserId = null;
                        }
                        await _context.SaveChangesAsync();

                        // Xóa giỏ hàng của người dùng
                        var carts = await _context.Carts.Where(c => c.UserId == user.UserName).ToListAsync();
                        foreach (var cart in carts)
                        {
                            var cartItems = await _context.CartItems.Where(ci => ci.CartId == cart.Id).ToListAsync();
                            _context.CartItems.RemoveRange(cartItems);
                            _context.Carts.Remove(cart);
                        }
                        await _context.SaveChangesAsync();

                        // Xóa các claims của người dùng
                        var claims = await _userManager.GetClaimsAsync(user);
                        foreach (var claim in claims)
                        {
                            await _userManager.RemoveClaimAsync(user, claim);
                        }

                        // Xóa người dùng
                        var result = await _userManager.DeleteAsync(user);
                        if (result.Succeeded)
                        {
                            await transaction.CommitAsync();
                            return result;
                        }
                        else
                        {
                            await transaction.RollbackAsync();
                            return result;
                        }
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        return IdentityResult.Failed(new IdentityError { Description = ex.Message });
                    }
                }
            }
            return IdentityResult.Failed(new IdentityError { Description = "User not found" });
        }

        public async Task<ApplicationUser> GetCurrentUserAsync()
        {
            return await _userManager.GetUserAsync(_httpContextAccessor.HttpContext.User);
        }

        public async Task AddUserDetailsAsync(ApplicationUser user, RegisterViewModel model)
        {
            var claims = new List<Claim>
            {
                new Claim("FullName", model.FullName),
                new Claim("DateOfBirth", model.DateOfBirth.ToString("yyyy-MM-dd")),
                new Claim("Address", model.Address ?? string.Empty)
            };

            if (!string.IsNullOrEmpty(model.AvatarUrl))
            {
                claims.Add(new Claim("AvatarUrl", model.AvatarUrl));
            }

            foreach (var claim in claims)
            {
                await _userManager.AddClaimAsync(user, claim);
            }
        }

        private UserDetailsViewModel ConvertToViewModel(ApplicationUser user)
        {
            var claims = _userManager.GetClaimsAsync(user).Result;
            return new UserDetailsViewModel
            {
                Id = user.Id,
                Email = user.Email,
                FullName = claims.FirstOrDefault(c => c.Type == "FullName")?.Value ?? user.UserName,
                PhoneNumber = user.PhoneNumber,
                Address = claims.FirstOrDefault(c => c.Type == "Address")?.Value,
                DateOfBirth = DateTime.TryParse(claims.FirstOrDefault(c => c.Type == "DateOfBirth")?.Value, out var dob) ? dob : DateTime.Now,
                AvatarUrl = claims.FirstOrDefault(c => c.Type == "AvatarUrl")?.Value ?? "/images/users/default-avatar.png",
                IsActive = user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.Now,
                CreatedAt = DateTime.Now
            };
        }

        public async Task<List<UserDetailsViewModel>> GetAllUserDetailsAsync()
        {
            var users = await _userManager.Users.ToListAsync();
            return users.Select(u => ConvertToViewModel(u)).ToList();
        }

        public async Task<UserDetailsViewModel> GetUserDetailsAsync(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            return user != null ? ConvertToViewModel(user) : null;
        }

        public async Task<List<string>> GetUserRolesAsync(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                return (await _userManager.GetRolesAsync(user)).ToList();
            }
            return new List<string>();
        }

        public async Task<List<string>> GetAllRolesAsync()
        {
            return await _roleManager.Roles.Select(r => r.Name).ToListAsync();
        }

        public async Task<IdentityResult> UpdateUserRolesAsync(string userId, List<string> newRoles)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return IdentityResult.Failed(new IdentityError { Description = "User not found" });

            var currentRoles = await _userManager.GetRolesAsync(user);
            var rolesToRemove = currentRoles.Except(newRoles).ToList();
            var rolesToAdd = newRoles.Except(currentRoles).ToList();

            var result = await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
            if (!result.Succeeded) return result;

            result = await _userManager.AddToRolesAsync(user, rolesToAdd);
            return result;
        }

        public async Task<IdentityResult> RemoveFromRoleAsync(string userId, string role)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user != null)
            {
                return await _userManager.RemoveFromRoleAsync(user, role);
            }
            return IdentityResult.Failed(new IdentityError { Description = "User not found" });
        }

        public async Task<List<ApplicationUser>> GetUsersInRoleAsync(string roleName)
        {
            return (await _userManager.GetUsersInRoleAsync(roleName)).ToList();
        }

        public async Task<IdentityResult> LockUserAsync(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return IdentityResult.Failed(new IdentityError { Description = "Không tìm thấy người dùng" });
            }

            // Đảm bảo tính năng khóa được bật cho người dùng này
            var enableLockoutResult = await _userManager.SetLockoutEnabledAsync(user, true);
            if (!enableLockoutResult.Succeeded)
            {
                return enableLockoutResult;
            }

            // Đặt thời gian khóa là 1 năm kể từ bây giờ
            var lockoutEndDate = DateTimeOffset.Now.AddYears(1);
            return await _userManager.SetLockoutEndDateAsync(user, lockoutEndDate);
        }

        public async Task<IdentityResult> UnlockUserAsync(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return IdentityResult.Failed(new IdentityError { Description = "Không tìm thấy người dùng" });
            }

            // Đặt thời gian khóa là null để mở khóa tài khoản
            var result = await _userManager.SetLockoutEndDateAsync(user, null);
            if (!result.Succeeded)
            {
                return result;
            }

            // Đặt lại số lần đăng nhập sai về 0
            return await _userManager.ResetAccessFailedCountAsync(user);
        }
    }
}
