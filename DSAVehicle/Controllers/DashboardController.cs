using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSAVehicle.Models;
using DSAVehicle.Data;
using Microsoft.AspNetCore.Mvc;

namespace DSAVehicle.Controllers
{
    public class DashboardController : Controller
    {
        public IActionResult Index()
    {
        return View();
    }
    }
}