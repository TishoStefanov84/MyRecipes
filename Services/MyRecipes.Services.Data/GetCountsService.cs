namespace MyRecipes.Services.Data
{
    using System.Linq;

    using MyRecipes.Data.Common.Repositories;
    using MyRecipes.Data.Models;
    using MyRecipes.Services.Data.Models;
    using MyRecipes.Web.ViewModels.Home;

    public class GetCountsService : IGetCountsService
    {
        private readonly IDeletableEntityRepository<Category> categoriesRepository;
        private readonly IDeletableEntityRepository<Ingredient> ingrediantsRepository;
        private readonly IDeletableEntityRepository<Recipe> recipesRepository;
        private readonly IRepository<Image> imagesRepository;

        public GetCountsService(
            IDeletableEntityRepository<Category> categoriesRepository,
            IDeletableEntityRepository<Ingredient> ingrediantsRepository,
            IDeletableEntityRepository<Recipe> recipesRepository,
            IRepository<Image> imagesRepository)
        {
            this.categoriesRepository = categoriesRepository;
            this.ingrediantsRepository = ingrediantsRepository;
            this.recipesRepository = recipesRepository;
            this.imagesRepository = imagesRepository;
        }

        public CountsDto GetCounts()
        {
            var data = new CountsDto
            {
                CategoriesCount = this.categoriesRepository.All().Count(),
                IngredientsCount = this.ingrediantsRepository.All().Count(),
                RecipesCount = this.recipesRepository.All().Count(),
                ImagesCount = this.imagesRepository.All().Count(),
            };

            return data;
        }
    }
}
