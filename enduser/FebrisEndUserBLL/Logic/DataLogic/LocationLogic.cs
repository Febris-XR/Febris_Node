// SPDX-FileCopyrightText: 2026 Febris
// SPDX-License-Identifier: AGPL-3.0-only
using Febris.ModelLibrary.Models.DataModels;
using Febris.UserNode.DataAccessLayer.Queries.DataQueries;
using Febris.SharedServices;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Febris.PrimaryLogicLayer.Logic.DataLogic
{
    public interface ILocationLogic
    {
        Task<List<Location>> Get();
        Task<Location> Get(long? id);
        Task<Location> Create(Location location);
        Task<Location> Update(Location location);
        Task<bool> Delete(long id);
    }

    public class LocationLogic : ILocationLogic
    {        
        private readonly ILocationQueries _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ClaimsPrincipal User;

        public LocationLogic(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
            User = _httpContextAccessor.HttpContext.User;
            _context = new LocationQueries();
        }

        // DI refactor
        public LocationLogic(IHttpContextAccessor httpContextAccessor, ILocationQueries context)
        {
            _httpContextAccessor = httpContextAccessor;
            User = _httpContextAccessor?.HttpContext?.User;
            _context = context;
        }


        #region Get
        public async Task<Location> Get(long? input)
        {
            Location output = new Location();
            try
            {
                //use input to find subscription
                output = await _context.Get(input);
                //output = subscription;
            }
            catch (System.Exception ex) { Febris.SharedServices.FebrisLog.Error(ex, "LocationLogic.Get(long?): suppressed exception"); }
            return output;
        }
        public async Task<Location> Get(Guid? input)
        {
            //bool output = true;
            Location output = new Location();
            try
            {
                //use input to find subscription
                output = await _context.Get(input);
                //output = subscription;
            }
            catch (System.Exception ex) { Febris.SharedServices.FebrisLog.Error(ex, "LocationLogic.Get(Guid?): suppressed exception"); }
            return output;
        }
        public async Task<List<Location>> Get()
        {
            List<Location> output = new List<Location>();
            try
            {
                //use input to find subscription
                output = await _context.Get();
                //output = subscription;
            }
            catch (System.Exception ex) { Febris.SharedServices.FebrisLog.Error(ex, "LocationLogic.Get(): suppressed exception"); }
            return output;
        }
        #endregion

        #region Create
        public async Task<Location> Create(Location input)
        {
            //if (await IsLockedOut())
            //{
            //    return null;
            //}
            Location output = new Location();
            try
            {
                if (!User.IsLocalAdmin()||!User.IsLocalFebrisAdmin())
                {
                    return null;
                }

                // Latitude and Longitude are stored exactly as supplied.
                //
                // They used to be overwritten here by a Geocoder lookup against
                // GeoDataUrls:GeoCoderServerAPIUrl. The only thing that ever read those
                // coordinates was the Leaflet map partial on Views/Location/Index.cshtml, which
                // the owner ruled out and ROADMAP 18 removed. That left a write with no reader
                // and an outbound call on a save path. On a shipped node the key is empty, so the
                // call could not succeed anyway and its failure was suppressed, meaning every
                // Location saved with zeroed coordinates and no error. Removed rather than
                // repaired, because the feature it fed is gone by ruling.
                output = await _context.Create(input);
            }
            catch (Exception ex)
            {
                Febris.SharedServices.FebrisLog.Error(ex);
            }
            return output;
        }
        #endregion

        #region Update
        public async Task<Location> Update(Location input)
        {
            Location output = new Location();
            try
            {
                if (!User.IsLocalAdmin() || !User.IsLocalFebrisAdmin())
                {
                    return null;
                }

                Location original = await _context.Get(input.Id);

                // The Geocoder backfill that used to sit here, firing whenever both coordinates
                // were zero, is gone with the map that read them. See Create above.

                //use input to find subscription
                output = await _context.Update(input);
                //output = subscription;
            }
            catch (System.Exception ex) { Febris.SharedServices.FebrisLog.Error(ex, "LocationLogic.Update: suppressed exception"); }
            return output;
        }
        #endregion

        #region Delete
        public async Task<bool> Delete(long input)
        {
            bool output = false;
            try
            {
                //use input to find subscription
                output = await _context.Delete(input);
                //output = subscription;
            }
            catch (System.Exception ex) { Febris.SharedServices.FebrisLog.Error(ex, "LocationLogic.Delete: suppressed exception"); }
            return output;
        }
        #endregion 
    }
        
}
