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

        public async Task PopulateDbWithRecipesAsync(int recipesCount)
        {
            var bag = new ConcurrentBag<RecipeDto>();

            Parallel.For(1, recipesCount, (i) =>
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

            foreach (var recipe in bag)
            {
                var categoryId = await this.GetOrCreateCategoryAsync(recipe.CategoryName);
                var recipeExists = this.recipesRepository
                    .AllAsNoTracking()
                    .Any(x => x.Name == recipe.RecipeName);

                if (recipeExists)
                {
                    continue;
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
                await this.recipesRepository.SaveChangesAsync();

                foreach (var ingr in recipe.Ingridients)
                {
                    var ingredientId = await this.GetOrCreateIngredientAsync(ingr.Key);
                    var quantity = ingr.Value;

                    var recipeIngredient = new RecipeIngredient
                    {
                        RecipeId = newRecipe.Id,
                        IngredientId = ingredientId,
                        Quantity = quantity,
                    };

                    await this.recipeIngredientsRepository.AddAsync(recipeIngredient);
                    await this.recipeIngredientsRepository.SaveChangesAsync();
                }

                var image = new Image
                {
                    Extension = recipe.OriginalUrl,
                    RecipeId = newRecipe.Id,
                };

                await this.imageRepository.AddAsync(image);
                await this.imageRepository.SaveChangesAsync();
            }
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
            var document = this.context
                .OpenAsync($"https://recepti.gotvach.bg/r-{id}")
                .GetAwaiter()
                .GetResult();

            if (document.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException();
            }

            var recipe = new RecipeDto();
            var recipeNameAndCategory = document
                .QuerySelectorAll("#recEntity > div.breadcrumb")
                .Select(x => x.TextContent)
                .FirstOrDefault()
                .Split(" »")
                .Reverse()
                .ToArray();

            recipe.CategoryName = recipeNameAndCategory[1];

            recipe.RecipeName = recipeNameAndCategory[0].TrimStart();

            var instructions = document
                .QuerySelectorAll(".text > p")
                .Select(x => x.TextContent)
                .ToList();
            var sb = new StringBuilder();

            foreach (var item in instructions)
            {
                sb.AppendLine(item);
            }

            recipe.Instructions = sb.ToString().TrimEnd();

            var timing = document.QuerySelectorAll(".mbox > .feat.small");

            if (timing.Length > 1)
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

            var portionsCount = document.QuerySelectorAll(".mbox > .feat > span").LastOrDefault().TextContent;
            recipe.PortionsCount = int.Parse(portionsCount);

            var imageUrl = document.QuerySelector("#newsGal > div.image > img").GetAttribute("src");
            recipe.OriginalUrl = imageUrl;

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

            return recipe;
        }
    }
}
