﻿using WebGoatCore.Models;
using WebGoatCore.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using WebGoatCore.ViewModels;

namespace WebGoatCore.Controllers
{
    public class CheckoutController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly CustomerRepository _customerRepository;
        private readonly ShipperRepository _shipperRepository;
        private readonly OrderRepository _orderRepository;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public CheckoutController(UserManager<IdentityUser> userManager, CustomerRepository customerRepository, IWebHostEnvironment webHostEnvironment, ShipperRepository shipperRepository, OrderRepository orderRepository)
        {
            _userManager = userManager;
            _customerRepository = customerRepository;
            _shipperRepository = shipperRepository;
            _webHostEnvironment = webHostEnvironment;
            _orderRepository = orderRepository;
        }

        [HttpGet]
        public IActionResult Checkout()
        {
            var model = new CheckoutViewModel();

            var username = _userManager.GetUserName(User);
            var customer = _customerRepository.GetCustomerByUsername(username);
            if (customer == null)
            {
                ModelState.AddModelError(string.Empty, "I can't identify you. Please log in and try again.");
                return View(model);
            }

            var creditCard = new CreditCard()
            {
                Filename = Path.Combine(_webHostEnvironment.WebRootPath, "StoredCreditCards.xml"),
                Username = username
            };

            try
            {
                creditCard.GetCardForUser();
                model.CreditCard = creditCard.Number;
                model.ExpirationMonth = creditCard.Expiry.Month;
                model.ExpirationYear = creditCard.Expiry.Year;
            }
            catch (NullReferenceException)
            {
            }

            if (!HttpContext.Session.TryGetInMemory<Cart>("Cart", out var cart) || cart.OrderDetails.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "You have no items in your cart.");
                return View(model);
            }
            model.Cart = cart;

            model.ShipTarget = customer.CompanyName;
            model.Address = customer.Address;
            model.City = customer.City;
            model.Region = customer.Region;
            model.PostalCode = customer.PostalCode;
            model.Country = customer.Country;
            model.AvailableExpirationYears = new List<int>();
            for (int i = 0; i <= 5; i++)
            {
                model.AvailableExpirationYears.Add(DateTime.Now.Year + i);
            }

            model.ShippingOptions = _shipperRepository.GetShippingOptions(cart.SubTotal);

            return View(model);
        }

        [HttpPost]
        public IActionResult Checkout(CheckoutViewModel model)
        {
            var username = _userManager.GetUserName(User);
            var customer = _customerRepository.GetCustomerByUsername(username);

            model.Cart = HttpContext.Session.GetInMemory<Cart>("Cart");

            var order = new Order
            {
                ShipVia = model.ShippingMethod,
                ShipName = model.ShipTarget,
                ShipAddress = model.Address,
                ShipCity = model.City,
                ShipRegion = model.Region,
                ShipPostalCode = model.PostalCode,
                ShipCountry = model.Country,
                OrderDetails = model.Cart.OrderDetails,
                CustomerId = customer.CustomerId,
                OrderDate = DateTime.Now,
                RequiredDate = DateTime.Now.AddDays(7),
                Freight = _shipperRepository.GetShipperByShipperId(model.ShippingMethod).GetShippingCost(model.Cart.SubTotal),
                //TODO: Throws an error if we don't set the date. Try to set it to null or something.
                ShippedDate = DateTime.Now.AddDays(3),
                EmployeeId = 1
            };

            var creditCard = new CreditCard()
            {
                Filename = Path.Combine(_webHostEnvironment.WebRootPath, "StoredCreditCards.xml"),
                Username = username
            };

            try
            {
                creditCard.GetCardForUser();
            }
            catch (NullReferenceException)
            {
            }

            //Get form of payment
            //If old card is null or if the number, month or year were changed then take what was on the form.
            if (creditCard.Number.Length <= 4)
            {
                creditCard.Number = model.CreditCard;
                creditCard.Expiry = new DateTime(model.ExpirationYear, model.ExpirationMonth, 1);
            }
            else
            {
                if (model.CreditCard.Substring(model.CreditCard.Length - 4) !=
                    creditCard.Number.Substring(creditCard.Number.Length - 4))
                {
                    creditCard.Number = model.CreditCard;
                }

                if (model.ExpirationMonth != creditCard.ExpiryMonth ||
                    model.ExpirationYear != creditCard.ExpiryYear)
                {
                    creditCard.Expiry = new DateTime(model.ExpirationYear, model.ExpirationMonth, 1);
                }
            }

            //Authorize payment through our bank or Authorize.net or someone.
            if (!creditCard.IsValid())
            {
                ModelState.AddModelError(string.Empty, "That card is not valid.  Please enter a valid card.");
                return View(model);
            }

            var approvalCode = creditCard.ChargeCard(order.Total);

            if (model.RememberCreditCard)
            {
                creditCard.SaveCardForUser();
            }

            order.Shipment = new Shipment()
            {
                ShipmentDate = DateTime.Today.AddDays(1),
                ShipperId = order.ShipVia,
                TrackingNumber = _shipperRepository.GetNextTrackingNumber(_shipperRepository.GetShipperByShipperId(order.ShipVia)),
            };

            //Create the order itself.
            int orderId = _orderRepository.CreateOrder(order);
            HttpContext.Session.SetInt32("OrderId", orderId);
            HttpContext.Session.Remove("Cart");

            //Create the payment record.
            _orderRepository.CreateOrderPayment(orderId, order.Total, creditCard.Number, creditCard.Expiry, approvalCode);

            return RedirectToAction("Receipt");
        }

        public IActionResult Receipt(int? id)
        {
            var orderId = HttpContext.Session.GetInt32("OrderId");
            if (id != null)
            {
                orderId = id;
            }

            if (orderId == null)
            {
                ModelState.AddModelError(string.Empty, "No order specified.  Please try again.");
                return View();
            }

            Order order;
            try
            {
                order = _orderRepository.GetOrderById(orderId.Value);
            }
            catch (InvalidOperationException)
            {
                ModelState.AddModelError(string.Empty, string.Format("Order {0} was not found.", orderId));
                return View();
            }

            return View(order);
        }

        public IActionResult Receipts()
        {
            var orders = GetOrdersOrAddError();
            if(orders == null)
            {
                return View();
            }

            return View(orders);
        }

        public IActionResult PackageTracking(string? carrier, string? trackingNumber)
        {
            var orders = GetOrdersOrAddError();
            if (orders == null)
            {
                return View();
            }

            var model = new PackageTrackingViewModel()
            { 
                SelectedCarrier = carrier,
                SelectedTrackingNumber = trackingNumber,
                Orders = orders
            };

            return View(model);
        }

        private ICollection<Order>? GetOrdersOrAddError()
        {
            var username = User.Identity.Name;
            var customer = _customerRepository.GetCustomerByUsername(username);

            if (customer == null)
            {
                ModelState.AddModelError(string.Empty, "I can't identify you. Please log in and try again.");
                return null;
            }

            return _orderRepository.GetAllOrdersByCustomerId(customer.CustomerId);
        }

        public IActionResult GoToExternalTracker(string carrier, string trackingNumber)
        {
            return Redirect(Order.GetPackageTrackingUrl(carrier, trackingNumber));
        }
    }
}