using bulkey.DataAccess.Repository.IRepository;
using bulkey.Models;
using bulkey.Models.ViewModels;
using bulkey.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe.Checkout;
using System.Security.Claims;


namespace bulkey.Areas.Customer.Controllers
{
    [Area("Customer")]
    [Authorize]
    public class CartController : Controller
    {
        private readonly IUnitOfWork _unitOfWork;
        [BindProperty]
        public ShoppingCartVM ShoppingCartVM { get; set; }
        public  int? OrderTotal { get; set; }
        public CartController(IUnitOfWork unitOfWork)
        {
            _unitOfWork = unitOfWork;
        }
        public IActionResult Index()
        {

            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);
            ShoppingCartVM = new ShoppingCartVM()
            {
                ListCart = _unitOfWork.ShoppingCart.GetAll(u => u.ApplicationUserId == claim.Value
                , includeProperties: "Product"),
                OrderHeader =new()
            };
            foreach(var cart in ShoppingCartVM.ListCart)
            {
                cart.Price = GetPriceBasedOnQuantity(cart.Count,cart.Product.ListPrice,
                    cart.Product.Price50,cart.Product.Price100);
                ShoppingCartVM.CartTotal += (cart.Price * cart.Count);
            }
            return View(ShoppingCartVM);
        }

        public IActionResult Summary()
        {

            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);

            ShoppingCartVM = new ShoppingCartVM()
            {
                ListCart = _unitOfWork.ShoppingCart.GetAll(u => u.ApplicationUserId == claim.Value,
                includeProperties: "Product"),
                OrderHeader = new()
            };
            ShoppingCartVM.OrderHeader.ApplicationUser = _unitOfWork.ApplicationUser.GetFirstOrDefault(
                u => u.Id == claim.Value);

            ShoppingCartVM.OrderHeader.Name = ShoppingCartVM.OrderHeader.ApplicationUser.Name;
            ShoppingCartVM.OrderHeader.PhoneNumber = ShoppingCartVM.OrderHeader.ApplicationUser.PhoneNumber;
            ShoppingCartVM.OrderHeader.StreetAddress = ShoppingCartVM.OrderHeader.ApplicationUser.StreetAddress;
            ShoppingCartVM.OrderHeader.City = ShoppingCartVM.OrderHeader.ApplicationUser.City;
            ShoppingCartVM.OrderHeader.State = ShoppingCartVM.OrderHeader.ApplicationUser.State;
            ShoppingCartVM.OrderHeader.PostalCode = ShoppingCartVM.OrderHeader.ApplicationUser.PostalCode;


            foreach (var cart in ShoppingCartVM.ListCart)
            {
                cart.Price = GetPriceBasedOnQuantity(cart.Count, cart.Product.ListPrice,
                    cart.Product.Price50, cart.Product.Price100);
                ShoppingCartVM.CartTotal += (cart.Price * cart.Count);
            }
            return View(ShoppingCartVM);
        }

        [HttpPost]
        [ActionName("SummaryPost")]
        [ValidateAntiForgeryToken]
        public IActionResult SummaryPost(ShoppingCartVM ShoppingCartVM)
        {

            var claimsIdentity = (ClaimsIdentity)User.Identity;
            var claim = claimsIdentity.FindFirst(ClaimTypes.NameIdentifier);
            ShoppingCartVM.ListCart = _unitOfWork.ShoppingCart.GetAll(u => u.ApplicationUserId == claim.Value
              , includeProperties: "Product");

            ShoppingCartVM.OrderHeader.PaymentStatus = SD.PaymentStatusPending;
            ShoppingCartVM.OrderHeader.OrderStatus = SD.StatusPending;
            ShoppingCartVM.OrderHeader.OrderDate = System.DateTime.Now;
            ShoppingCartVM.OrderHeader.ApplicationUserId = claim.Value;

            foreach (var cart in ShoppingCartVM.ListCart)
            {
                cart.Price = GetPriceBasedOnQuantity(cart.Count, cart.Product.ListPrice,
                    cart.Product.Price50, cart.Product.Price100);
                ShoppingCartVM.OrderHeader.OrderTotal += (cart.Price * cart.Count);
            }

            _unitOfWork.OrderHeader.Add(ShoppingCartVM.OrderHeader);
            _unitOfWork.Save();

            foreach (var cart in ShoppingCartVM.ListCart)
            {
                OrderDetail orderDetail = new OrderDetail()
                {
                    ProductId = cart.ProductId,
                    OrderId = ShoppingCartVM.OrderHeader.Id,
                    Price = cart.Price,
                    Count = cart.Count
                };
                _unitOfWork.OrderDetail.Add(orderDetail);
                _unitOfWork.Save();
            }

            //stripe payment

            var domain = "https://localhost:44359/";
            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string>
            {
                "card",
            },
                LineItems = new List<SessionLineItemOptions>()
                 ,
                Mode = "payment",
                SuccessUrl = domain+$"customer/cart/orderConfirmation?id={ShoppingCartVM.OrderHeader.Id}",
                CancelUrl = domain + $"customer/cart/index",
            };
            foreach (var option in ShoppingCartVM.ListCart)
            {
                var sessionLineItem = new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        UnitAmount = (long)(option.Price * 100),
                        Currency = "usd",
                        ProductData = new SessionLineItemPriceDataProductDataOptions

                        {
                            Name = option.Product.Title,
                        },

                    },
                    Quantity = option.Count,
                };
                options.LineItems.Add(sessionLineItem);


            }


            var service = new SessionService();
            Session session = service.Create(options);
            _unitOfWork.OrderHeader.UpdateStripePayment(ShoppingCartVM.OrderHeader.Id, session.Id,session.PaymentIntentId);
            _unitOfWork.Save();
            Response.Headers.Add("Location", session.Url);
            return new StatusCodeResult(303);



        }

        public IActionResult orderConfirmation(int id)
        {
            OrderHeader orderHeader = _unitOfWork.OrderHeader.GetFirstOrDefault(u=>u.Id == id);
            var service = new SessionService();
            Session session = service.Get(orderHeader.SessionId);
            //check the stripe status
            if(session.PaymentStatus.ToLower() == "paid")
            {

                _unitOfWork.OrderHeader.UpdateStatus(id, SD.StatusApproved, SD.PaymentStatusApproved);
                _unitOfWork.Save();
                
            }
            List<ShoppingCart>shoppingCarts=_unitOfWork.ShoppingCart.GetAll(u=>u.ApplicationUserId==
            orderHeader.ApplicationUserId).ToList();
            _unitOfWork.ShoppingCart.RemoveRange(shoppingCarts);
            _unitOfWork.Save();

            return RedirectToAction("Index", "Home");
        }
        public IActionResult Plus(int cartId)
        {
            var cart=_unitOfWork.ShoppingCart.GetFirstOrDefault(u=>u.Id == cartId);
            _unitOfWork.ShoppingCart.IncrementCount(cart, 1);
            _unitOfWork.Save();
            return RedirectToAction(nameof(Index));
        }

        public IActionResult minus(int cartId)
        {
            var cart = _unitOfWork.ShoppingCart.GetFirstOrDefault(u => u.Id == cartId);
            if (cart.Count <= 1)
            {
                _unitOfWork.ShoppingCart.Remove(cart);
            }
            else
            {
                _unitOfWork.ShoppingCart.DecrementCount(cart, 1);
                _unitOfWork.Save();
               
            }
            return RedirectToAction(nameof(Index));
        }

        public IActionResult Remove(int cartId)
        {
            var cart = _unitOfWork.ShoppingCart.GetFirstOrDefault(u => u.Id == cartId);
            _unitOfWork.ShoppingCart.Remove(cart);
            _unitOfWork.Save();
            return RedirectToAction(nameof(Index));
        }






        private double GetPriceBasedOnQuantity(double Quantity,double price,double price50,double price100 )
        {
            if (Quantity <= 50)
            {
                return price;
            }
            else
            {
                if (Quantity <= 50)
                {
                    return price50;
                }
                else
                {
                    return price100;
                }
            }
        }
    }
}
