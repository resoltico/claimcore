export const ProductBrand = ({ wordmarkClassName }: { wordmarkClassName?: string }) => (
  <>
    <img className="product-mark" src="/brand.svg" alt="" />
    <span className={wordmarkClassName}>ClaimCore</span>
  </>
);
