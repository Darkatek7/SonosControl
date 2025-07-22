using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace SonosControl.Web.Models
{
    public class ApplicationUser : IdentityUser
    {
        [PersonalData]
        [MaxLength(100)]
        public string FirstName { get; set; }

        [PersonalData]
        [MaxLength(100)]
        public string LastName { get; set; }
    }
}