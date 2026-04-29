using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyfirstASP_NET.Models;
using MyfirstASP_NET.Data;
using Microsoft.AspNetCore.Mvc;

namespace MyfirstASP_NET.Controllers
{
    public class DashboardController : Controller
    {
        public IActionResult Index()
    {
        return View();
    }
    }
}