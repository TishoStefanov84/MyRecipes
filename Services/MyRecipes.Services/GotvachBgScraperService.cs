namespace MyRecipes.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    using AngleSharp;
    using MyRecipes.Data.Common.Repositories;
    using MyRecipes.Data.Models;
    using MyRecipes.Services.Models;

    public class GotvachBgScraperService : IGotvachBgScraperService
    {
        private const string BaseUrl = "https://recepti.gotvach.bg/r-{0}";
        private const string Preparation = "Приготвяне";
        private const string Cooking = "Готвене";
        private const string Minutes = " мин.";

        private readonly IConfiguration config;
        private readonly IBrowsingContext context;
        private readonly IDeletableEntityRepository<Category> categoriesRepository;
        private readonly IDeletableEntityRepository<Recipe> recipesRepository;
        private readonly IDeletableEntityRepository<Ingredient> ingredientsRepository;
        private readonly IRepository<RecipeIngredient> recipeIngredientsRepository;
        private readonly IRepository<Image> imageRepository;

        public GotvachBgScraperService(
            IDeletableEntityRepository<Category> categoriesRepository,
            IDeletableEntityRepository<Recipe> recipesRepository,
            IDeletableEntityRepository<Ingredient> ingredientsRepository,
            IRepository<RecipeIngredient> recipeIngredientsRepository,
            IRepository<Image> imageRepository)
        {
            this.config = Configuration.Default.WithDefaultLoader();
            this.context = BrowsingContext.New(this.config);

            this.categoriesRepository = categoriesRepository;
            this.recipesRepository = recipesRepository;
            this.ingredientsRepository = ingredientsRepository;
            this.recipeIngredientsRepository = recipeIngredientsRepository;
            this.imageRepository = imageRepository;
        }

        public async Task ImportRecipesAsync(int fromId = 1, int toId = 1000)
        {
            var bag = this.ScrapeRecipes(fromId, toId);
            int addedCount = 0;
            foreach (var recipe in bag)
            {
                var categoryId = await this.GetOrCreateCategoryAsync(recipe.CategoryName);

                if (recipe.CookingTime.Days >= 1)
                {
                    recipe.CookingTime = new TimeSpan(23, 59, 59);
                }

                if (recipe.PreparationTime.Days >= 1)
                {
                    recipe.PreparationTime = new TimeSpan(23, 59, 59);
                }

                var newRecipe = new Recipe
                {
                    Name = recipe.RecipeName,
                    Instructions = recipe.Instructions,
                    PreparationTime = recipe.PreparationTime,
                    CookingTime = recipe.CookingTime,
                    PortionsCount = recipe.PortionsCount,
                    OriginalUrl = recipe.OriginalUrl,
                    CategoryId = categoryId,
                };

                await this.recipesRepository.AddAsync(newRecipe);

                foreach (var ingr in recipe.Ingridients)
                {
                    var ingredientId = await this.GetOrCreateIngredientAsync(ingr.Key);
                    var quantity = ingr.Value;

                    var recipeIngredient = new RecipeIngredient
                    {
                        Recipe = newRecipe,
                        IngredientId = ingredientId,
                        Quantity = quantity,
                    };

                    await this.recipeIngredientsRepository.AddAsync(recipeIngredient);
                }

                var image = new Image
                {
                    RemoteImageUrl = recipe.ImageUrl,
                    Recipe = newRecipe,
                };

                await this.imageRepository.AddAsync(image);

                if (++addedCount % 1000 == 0)
                {
                    await this.recipesRepository.SaveChangesAsync();
                }
            }

            await this.recipesRepository.SaveChangesAsync();
        }

        private ConcurrentBag<RecipeDto> ScrapeRecipes(int fromId, int toId)
        {
            var bag = new ConcurrentBag<RecipeDto>();
            Parallel.For(fromId, toId + 1, i =>
            {
                try
                {
                    var recipe = this.GetRecipeInfo(i);
                    bag.Add(recipe);
                }
                catch
                {
                }
            });

            return bag;
        }

        private async Task<int> GetOrCreateIngredientAsync(string ingrName)
        {
            var ingr = this.ingredientsRepository
                .AllAsNoTracking()
                .FirstOrDefault(x => x.Name == ingrName);

            if (ingr == null)
            {
                ingr = new Ingredient
                {
                    Name = ingrName,
                };

                await this.ingredientsRepository.AddAsync(ingr);
                await this.ingredientsRepository.SaveChangesAsync();
            }

            return ingr.Id;
        }

        private async Task<int> GetOrCreateCategoryAsync(string categoryName)
        {
            var category = this.categoriesRepository
                .AllAsNoTracking()
                .FirstOrDefault(x => x.Name == categoryName);

            if (category == null)
            {
                category = new Category
                {
                    Name = categoryName,
                };

                await this.categoriesRepository.AddAsync(category);
                await this.categoriesRepository.SaveChangesAsync();
            }

            return category.Id;
        }

        private RecipeDto GetRecipeInfo(int id)
        {
            var url = string.Format(BaseUrl, id);

            var document = this.context
                .OpenAsync(url)
                .GetAwaiter()
                .GetResult();

            if (document.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException();
            }

            var recipe = new RecipeDto();

            var recipeNameCategory = document
                .QuerySelectorAll("#recEntity > div.breadcrumb")
                .Select(x => x.TextContent)
                .FirstOrDefault()
                .Split(" »", StringSplitOptions.RemoveEmptyEntries)
                .Reverse()
                .ToArray();

            recipe.CategoryName = recipeNameCategory[1].Trim();

            recipe.RecipeName = recipeNameCategory[0].Trim();

            var instructions = document
                .QuerySelectorAll(".text > p")
                .Select(x => x.TextContent)
                .ToList();

            recipe.Instructions = string.Join(Environment.NewLine, instructions);

            var timing = document.QuerySelectorAll(".mbox > .feat.small");

            if (timing.Length > 0)
            {
                var prepTime = timing[0]
                    .TextContent
                    .Replace(Preparation, string.Empty)
                    .Replace(Minutes, string.Empty);

                var totalMinutes = int.Parse(prepTime);

                recipe.PreparationTime = TimeSpan.FromMinutes(totalMinutes);
            }

            if (timing.Length > 1)
            {
                var cookTime = timing[1]
                    .TextContent
                    .Replace(Cooking, string.Empty)
                    .Replace(Minutes, string.Empty);

                var totalMinutes = int.Parse(cookTime);

                recipe.CookingTime = TimeSpan.FromMinutes(totalMinutes);
            }

            var portionsCount = document
                .QuerySelectorAll(".mbox > .feat > span")
                .LastOrDefault()
                ?.TextContent;

            recipe.PortionsCount = int.Parse(portionsCount);

            recipe.ImageUrl = document
                .QuerySelector("#newsGal > div.image > img")
                .GetAttribute("src");

            var elements = document.QuerySelectorAll(".products > ul > li");

            foreach (var element in elements)
            {
                var ingridientInfo = element.TextContent.Split(" -  ", 2);
                if (ingridientInfo.Length < 2)
                {
                    throw new InvalidOperationException();
                }

                var ingridientName = ingridientInfo[0].Trim();
                var ingridientQuantity = ingridientInfo[1].Trim();

                recipe.Ingridients[ingridientName] = ingridientQuantity;
            }

            recipe.OriginalUrl = url;

            return recipe;
        }
    }
}
